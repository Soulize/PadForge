using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Xml.Linq;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Mappings table's rows are Tab stops for keyboard and screen reader
    /// users, under the rule #61 set for the tab's closed pickers: the keys a
    /// keyboard-mapped slot uses never change the table. Tab skipped the
    /// table, since WPF makes a DataGrid row unfocusable (DataGridRow.cs,
    /// FocusableProperty false) and a compact row hides the cells that could
    /// take focus. The Enter/Space toggle meant for a focused row never ran
    /// for the same reason (tutorial probe, 2026-10-28).
    /// </summary>
    public class MappingRowKeyboardTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Theory]
        [InlineData(Key.Up)]
        [InlineData(Key.Down)]
        [InlineData(Key.Left)]
        [InlineData(Key.Right)]
        [InlineData(Key.PageUp)]
        [InlineData(Key.PageDown)]
        [InlineData(Key.Home)]
        [InlineData(Key.End)]
        [InlineData(Key.A)]
        [InlineData(Key.Z)]
        [InlineData(Key.D0)]
        [InlineData(Key.D9)]
        [InlineData(Key.NumPad0)]
        [InlineData(Key.NumPad9)]
        public void AKeyASlotMaps_ChangesNoClosedPickerAndNoRow(Key key)
        {
            Assert.True(PadPage.KeyStaysOutOfMappings(key));
            Assert.Equal(PadPage.MappingRowKeyAction.Ignore, PadPage.RowKeyAction(key));
        }

        [Theory]
        [InlineData(Key.Tab)]
        [InlineData(Key.Escape)]
        [InlineData(Key.F4)]
        [InlineData(Key.Enter)]
        [InlineData(Key.Space)]
        public void TheKeysThatMoveFocusOrOpen_StayLive(Key key)
            => Assert.False(PadPage.KeyStaysOutOfMappings(key));

        [Theory]
        [InlineData(Key.Enter, (int)PadPage.MappingRowKeyAction.Toggle)]
        [InlineData(Key.Space, (int)PadPage.MappingRowKeyAction.Toggle)]
        [InlineData(Key.Tab, (int)PadPage.MappingRowKeyAction.PassOn)]
        [InlineData(Key.Escape, (int)PadPage.MappingRowKeyAction.PassOn)]
        public void ARow_OpensAndClosesOnEnterOrSpace_AndLetsFocusKeysThrough(Key key, int action)
            => Assert.Equal((PadPage.MappingRowKeyAction)action, PadPage.RowKeyAction(key));

        /// <summary>The row style makes each row a Tab stop.</summary>
        [Fact]
        public void TheMappingRows_AreTabStops()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var page = XDocument.Load(Path.Combine(RepoRoot(), "PadForge.App", "Views", "PadPage.xaml"));
            var grid = page.Descendants().Single(e => e.Name.LocalName == "DataGrid" && (string)e.Attribute(x + "Name") == "MappingDataGrid");
            var rowStyle = grid.Elements().Single(e => e.Name.LocalName == "DataGrid.RowStyle").Elements().Single();
            Assert.Contains(rowStyle.Elements(), s => s.Name.LocalName == "Setter"
                && (string)s.Attribute("Property") == "Focusable" && (string)s.Attribute("Value") == "True");
        }

        /// <summary>A click on a row leaves keyboard focus where the window's
        /// click handler puts it (MainWindow.ClickTargetPreservesFocus, which
        /// clears it for a row), so a key the slot maps, pressed after the
        /// click to test the mapping, never reaches the table. The click
        /// handler called row.Focus(), which did nothing while rows could not
        /// take focus and would hand them the key once they can.</summary>
        [Fact]
        public void AClickOnARow_DoesNotFocusIt()
        {
            string code = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", "PadPage.xaml.cs"));
            int start = code.IndexOf("void MappingRow_PreviewMouseLeftButtonDown(", StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = code.IndexOf("\n        }", start, StringComparison.Ordinal);
            Assert.True(end > start);
            Assert.DoesNotContain(".Focus()", code.Substring(start, end - start));
        }
    }
}
