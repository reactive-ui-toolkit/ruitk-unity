#if UNITY_EDITOR
namespace Ruitk.Builder
{
    /// <summary>
    /// Cross-pane drag state (POC 6.6 reimplemented over pointer events, per the
    /// porting note): a palette or markup row arms a payload on pointer-down;
    /// canvas rows hit-test and band-hint while armed; the drop consumes it on
    /// pointer-up. Payloads: "element:Tag", "component:Tag", "hook:name",
    /// "stylemod:name", "utilmod:name", "move:&lt;path&gt;:&lt;rowIdx&gt;".
    /// </summary>
    internal static class BuilderDragService
    {
        public static string Payload;

        public static bool Active => !string.IsNullOrEmpty(Payload);

        public static void Arm(string payload) => Payload = payload;

        public static string Take()
        {
            string payload = Payload;
            Payload = null;
            return payload;
        }

        public static void Cancel() => Payload = null;
    }
}
#endif
