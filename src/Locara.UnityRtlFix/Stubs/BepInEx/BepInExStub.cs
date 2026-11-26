using System;
using BepInEx.Logging;
using UnityEngine;

namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public void LogInfo(object data) { }
        public void LogWarning(object data) { }
        public void LogError(object data) { }
    }
}

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class BepInPlugin : Attribute
    {
        public string GUID { get; }
        public string Name { get; }
        public Version Version { get; }

        public BepInPlugin(string GUID, string Name, string Version)
        {
            this.GUID = GUID;
            this.Name = Name;
            this.Version = new Version(Version);
        }
    }

    public static class Paths
    {
        public static string ConfigPath => "";
    }

    public abstract class BaseUnityPlugin : MonoBehaviour
    {
        protected ManualLogSource Logger => new ManualLogSource();
    }
}
