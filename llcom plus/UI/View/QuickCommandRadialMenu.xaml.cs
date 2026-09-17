using System;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus
{
    public partial class QuickCommandRadialMenu : UserControl
    {
        public event EventHandler CopyRequested;
        public event EventHandler DeleteRequested;
        public QuickCommandRadialMenu() => InitializeComponent();
        internal Point AnchorCenter => new Point(10, Height / 2);
        internal void SetCanCopy(bool allowed) => CopyButton.IsEnabled = allowed;
        internal void FocusFirstAction() => CopyButton.Focus();
        private void Copy_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke(this, EventArgs.Empty);
        private void Delete_Click(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this, EventArgs.Empty);
    }
}
