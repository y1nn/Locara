using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Locara.Core;

public sealed partial class ApiProvider
{
    private static byte[]? CertificatePin(ProviderOptions options, Uri address)
    {
        if (string.IsNullOrWhiteSpace(options.CertificateSha256)) return null;
        if (!options.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) || address.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("بصمة الشهادة متاحة لاتصال OpenAI-compatible عبر HTTPS.");

        string hex = string.Concat(options.CertificateSha256.Where(value => value != ':' && !char.IsWhiteSpace(value)));
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException("أدخل بصمة SHA-256 لشهادة الخادم، وهي 64 خانة سداسية عشرية.");
        return Convert.FromHexString(hex);
    }

    private static async Task<HttpResponseMessage> SendRequest(HttpRequestMessage request, ProviderOptions options,
        CancellationToken cancellation, bool inspectPrivateCertificate = false)
    {
        var address = request.RequestUri!;
        byte[]? pin = CertificatePin(options, address);
        bool inspect = inspectPrivateCertificate && pin == null && address.Scheme == Uri.UriSchemeHttps
            && options.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase);
        string? observedFingerprint = null;
        // Keep sockets authenticated by a private certificate separate from the default trust pool.
        using var requestClient = pin == null && !inspect ? null : new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
            {
                if (pin != null) return MatchesCertificatePin(pin, certificate, chain, errors);
                if (errors == SslPolicyErrors.None) return true;
                if (certificate != null && IsUntrustedSelfSignedCertificate(certificate, chain, errors))
                    observedFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                return false;
            }
        }) { Timeout = Client.Timeout };

        try
        {
            return await (requestClient ?? Client).SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation);
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            if (observedFingerprint != null)
                throw new PrivateServerCertificateException(address, observedFingerprint, error);
            string detail = pin == null
                ? "تحقق من اسم الخادم وصلاحية شهادته وثقة Windows بها."
                : "تحقق من تطابق بصمة الشهادة وصلاحيتها لهذا العنوان. إذا جددت الشهادة، أفرغ حقل البصمة واضغط «جلب النماذج» لمراجعتها.";
            if (pin == null && !inspect && options.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase))
                detail += " إذا كانت الشهادة ذاتية التوقيع، اضغط «جلب النماذج» لمراجعتها.";
            throw new HttpRequestException(HttpRequestError.SecureConnectionError,
                $"تعذّر التحقق من اتصال HTTPS مع {address.Authority}. {detail}", error);
        }
    }

    private static bool MatchesCertificatePin(byte[] pin, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate == null || !CryptographicOperations.FixedTimeEquals(pin, certificate.GetCertHash(HashAlgorithmName.SHA256)))
            return false;
        return errors == SslPolicyErrors.None || IsUntrustedSelfSignedCertificate(certificate, chain, errors);
    }

    private static bool IsUntrustedSelfSignedCertificate(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.RemoteCertificateChainErrors && chain is { ChainElements.Count: 1, ChainStatus.Length: > 0 }
        && certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
        && chain.ChainStatus.All(status => status.Status == X509ChainStatusFlags.UntrustedRoot);
}

public sealed class PrivateServerCertificateException(Uri endpoint, string fingerprint, Exception cause)
    : HttpRequestException(HttpRequestError.SecureConnectionError,
        $"تعذّر التحقق من اتصال HTTPS مع {endpoint.Authority}. قدّم الخادم شهادة ذاتية التوقيع.", cause)
{
    public Uri Endpoint { get; } = endpoint;
    public string Fingerprint { get; } = fingerprint;
}
