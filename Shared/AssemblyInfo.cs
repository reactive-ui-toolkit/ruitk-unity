using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Ruitk.Runtime")]
[assembly: InternalsVisibleTo("Ruitk.Ugui")]

#if UNITY_EDITOR
[assembly: InternalsVisibleTo("Ruitk.Editor")]
[assembly: InternalsVisibleTo("Ruitk.Ugui.Tests")]
[assembly: InternalsVisibleTo("Ruitk.Builder.Editor")]
#endif
