using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// ES-modules campaign (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md M1, U-05):
    /// <see cref="MarkupTokenizer.ReadTagName"/> accepts exactly one interior <c>.</c> for
    /// namespace-import-qualified tags (<c>&lt;X.Comp/&gt;</c>). Plain (undotted) tags must keep
    /// tokenizing byte-identically — the family scanner corpus's fileScan tier is the primary
    /// watchdog for that; these pin the new dotted behavior directly.
    /// </summary>
    public sealed class MarkupTokenizerDottedTagTests
    {
        private static string ReadTagName(string source)
            => new MarkupTokenizer(source).ReadTagName();

        [Fact]
        public void PlainTag_UnaffectedByDotSupport()
        {
            Assert.Equal("VisualElement", ReadTagName("VisualElement style={x}>"));
        }

        [Fact]
        public void DottedTag_OneInteriorDot_ReadInFull()
        {
            Assert.Equal("Shapes.Circle", ReadTagName("Shapes.Circle radius={5}/>"));
        }

        [Fact]
        public void DottedTag_SelfClosing_ReadsUpToSlash()
        {
            Assert.Equal("X.Comp", ReadTagName("X.Comp/>"));
        }

        [Fact]
        public void DottedTag_TrailingDotWithNothingAfter_StopsBeforeDot()
        {
            // "Foo." with no identifier after the dot is not a qualified tag — the dot is left
            // for the existing unknown-tag/attribute diagnostics path, not silently consumed.
            Assert.Equal("Foo", ReadTagName("Foo.>"));
        }

        [Fact]
        public void DottedTag_TwoInteriorDots_OnlyFirstSegmentPairConsumed()
        {
            // Exactly ONE interior dot is legal (U-05) — "A.B.C" reads "A.B", leaving ".C" for
            // the caller (which will surface it as malformed via the existing tag-close checks).
            Assert.Equal("A.B", ReadTagName("A.B.C>"));
        }

        [Fact]
        public void DottedTag_DotFollowedByDigit_NotConsumed()
        {
            // A dot followed by a non-identifier-start char (e.g. a digit) is not a qualified
            // tag — could be markup text like `<Foo.5 />` (malformed either way, but the reader
            // must not swallow the dot speculatively).
            Assert.Equal("Foo", ReadTagName("Foo.5>"));
        }
    }
}
