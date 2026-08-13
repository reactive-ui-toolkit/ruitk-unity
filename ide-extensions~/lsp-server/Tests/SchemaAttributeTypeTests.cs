using System.Linq;
using Ruitk.Language;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// Pins schema attribute types that must match the RUNTIME prop surface — the
    /// schema's <c>type</c> feeds <see cref="PropsTypeAdapter.GetPropType"/> straight
    /// into the virtual document's typed props check, so a wrong entry produces a
    /// false CS0029 on code the Unity build accepts (field find: TabView's
    /// <c>selectedIndexChanged</c> claimed <c>Action</c> while the runtime prop is
    /// <c>TabIndexEventHandler</c>; EnumField's <c>enumType</c> claimed <c>Type</c>
    /// while the adapter consumes an assembly-qualified name STRING).
    /// </summary>
    public sealed class SchemaAttributeTypeTests
    {
        private static string? AttrType(string element, string attribute)
        {
            var el = new UitkxSchema().TryGetElement(element, backend: null);
            Assert.NotNull(el);
            var attr = el!.Attributes.FirstOrDefault(a => a.Name == attribute);
            Assert.NotNull(attr);
            return attr!.Type;
        }

        [Fact]
        public void TabView_SelectedIndexChanged_IsTabIndexEventHandler()
            => Assert.Equal("TabIndexEventHandler", AttrType("TabView", "selectedIndexChanged"));

        [Fact]
        public void TabView_ActiveTabChanged_IsTabChangedEventHandler()
            => Assert.Equal("TabChangedEventHandler", AttrType("TabView", "activeTabChanged"));

        [Fact]
        public void EnumField_EnumType_IsAssemblyQualifiedNameString()
            => Assert.Equal("string", AttrType("EnumField", "enumType"));
    }
}
