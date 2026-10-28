using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using PadForge.Engine.Menus;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A menu cell's type box (PadPage.xaml, the Cell Bindings row) binds
    /// ItemsSource to BindingKindOptions and SelectedValue to BindingKind.
    /// Picking Controller Button on a new cell left the box blank inside
    /// WPF's red validation outline while the button picker beside it
    /// appeared (tutorial probe, 2026-10-28). These tests bind a real
    /// ComboBox the same way and pick each kind.
    /// </summary>
    public class MenuCellKindComboTests
    {
        [Fact]
        public void PickingEachKind_LeavesTheBoxOnThatKind()
        {
            RunSta(() =>
            {
                var editor = new MenuEditorItem(new MenuDefinitionEntry { MenuId = 1, CellCount = 4 })
                {
                    MacroNamesProvider = () => new[] { "Jump" },
                };
                var cell = editor.Cells.First(c => c.Index == 1);
                var (combo, window) = BindTypeBox(cell);
                try
                {
                    foreach (int kind in new[] { 2, 1, MenuCellItem.MacroKind, 0, 2 })
                        PickAndCheck(combo, window, cell, kind);
                }
                finally { window.Close(); }
            });
        }

        /// <summary>A cell bound only through mapping rows lists a read-only
        /// entry that leaves the list once a direct binding exists, so the
        /// pick there changes the list's own contents.</summary>
        [Fact]
        public void PickingAKind_WhereTheListLosesAnEntry_LeavesTheBoxOnThatKind()
        {
            RunSta(() =>
            {
                var editor = new MenuEditorItem(new MenuDefinitionEntry { MenuId = 1, CellCount = 4 });
                editor.RowBoundProvider = (menu, index) => index == 1;
                var cell = editor.Cells.First(c => c.Index == 1);
                Assert.Equal(MenuCellItem.RowBoundKind, cell.BindingKind);
                var (combo, window) = BindTypeBox(cell);
                try
                {
                    PickAndCheck(combo, window, cell, 1);
                    Assert.DoesNotContain(Options(combo), o => o.Value == MenuCellItem.RowBoundKind);
                    PickAndCheck(combo, window, cell, 2);
                }
                finally { window.Close(); }
            });
        }

        private static void PickAndCheck(ComboBox combo, Window window, MenuCellItem cell, int kind)
        {
            combo.SelectedItem = Options(combo).Single(o => o.Value == kind);
            Pump(window);
            Assert.Equal(kind, cell.BindingKind);
            Assert.False(Validation.GetHasError(combo), $"kind {kind}: the box shows a validation error");
            Assert.Equal(kind, combo.SelectedValue);
            Assert.Equal(kind, Assert.IsType<MenuIntOption>(combo.SelectedItem).Value);
        }

        private static IEnumerable<MenuIntOption> Options(ComboBox combo)
            => ((IEnumerable<MenuIntOption>)combo.ItemsSource).ToList();

        /// <summary>The PadPage.xaml bindings of the type box, verbatim.</summary>
        private static (ComboBox, Window) BindTypeBox(MenuCellItem cell)
        {
            var combo = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Value", DataContext = cell };
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MenuCellItem.BindingKindOptions)));
            combo.SetBinding(Selector.SelectedValueProperty,
                new Binding(nameof(MenuCellItem.BindingKind)) { Mode = BindingMode.TwoWay });
            var window = new Window
            {
                Content = combo, Width = 400, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, WindowStyle = WindowStyle.None,
            };
            window.Show();
            Pump(window);
            return (combo, window);
        }

        private static void Pump(Window window)
            => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(20000), "STA test body timed out");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
