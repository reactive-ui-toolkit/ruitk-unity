namespace Ruitk.Bench
{
    public enum BenchOutputTarget
    {
        Auto,
        Editor,
        Runtime,
    }

    /// <summary>
    /// In-memory override slots for <c>BenchPerSecondLogger.BeginRun</c>; null
    /// means "use the live environment value". Never serialized - the resolved
    /// values land on <c>BenchEnv</c>, which is what reaches disk. Deliberately
    /// NOT [Serializable]: Unity's serializer cannot represent the nullable
    /// fields, and 6.5's serialization analyzer (UAC1001) rightly flags any
    /// [Serializable] type that makes that false promise.
    /// </summary>
    public struct BenchEnvOverrides
    {
        public bool? isEditor;
        public bool? isDevelopmentBuild;
        public string productName;
        public string platform;
        public string graphicsDevice;
        public string deviceModel;
        public string deviceName;
        public int? screenWidth;
        public int? screenHeight;
        public int? systemMemoryMB;
    }
}
