using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace llcom_plus.Tools
{
    public static class CipherSuitePicker
    {
        private static readonly string[] FallbackCipherSuites =
        {
            "TLS_AES_256_GCM_SHA384",
            "TLS_CHACHA20_POLY1305_SHA256",
            "TLS_AES_128_GCM_SHA256",
            "TLS_AES_128_CCM_SHA256",
            "TLS_AES_128_CCM_8_SHA256",
            "ECDHE-ECDSA-AES256-GCM-SHA384",
            "ECDHE-RSA-AES256-GCM-SHA384",
            "ECDHE-ECDSA-CHACHA20-POLY1305",
            "ECDHE-RSA-CHACHA20-POLY1305",
            "ECDHE-ECDSA-AES128-GCM-SHA256",
            "ECDHE-RSA-AES128-GCM-SHA256",
            "DHE-RSA-AES256-GCM-SHA384",
            "DHE-RSA-AES128-GCM-SHA256",
            "ECDHE-RSA-AES256-SHA384",
            "ECDHE-RSA-AES128-SHA256",
            "AES256-GCM-SHA384",
            "AES128-GCM-SHA256",
            "AES256-SHA256",
            "AES128-SHA256"
        };

        private static readonly object cacheLock = new object();
        private static List<string> cachedCipherSuites;

        public static void Attach(ComboBox comboBox, Func<string, string> getResourceText)
        {
            if (comboBox == null)
                return;

            comboBox.Tag = getResourceText;
            comboBox.IsEditable = true;
            comboBox.IsReadOnly = true;
            comboBox.IsTextSearchEnabled = false;
            comboBox.MaxDropDownHeight = 320;
            comboBox.StaysOpenOnEdit = true;
            comboBox.DropDownOpened -= ComboBox_DropDownOpened;
            comboBox.DropDownOpened += ComboBox_DropDownOpened;

            RebuildItems(comboBox);
            UpdateDisplayText(comboBox);
        }

        private static void ComboBox_DropDownOpened(object sender, EventArgs e)
        {
            UpdateDisplayText(sender as ComboBox);
        }

        private static void RebuildItems(ComboBox comboBox)
        {
            comboBox.Items.Clear();

            var selected = new HashSet<string>(
                SplitCipherSuites(Global.setting.tcpClientSslCipherSuites),
                StringComparer.OrdinalIgnoreCase);
            var allSuites = GetAvailableCipherSuites().ToList();
            foreach (var cipherSuite in selected)
            {
                if (!allSuites.Contains(cipherSuite, StringComparer.OrdinalIgnoreCase))
                    allSuites.Add(cipherSuite);
            }

            foreach (var cipherSuite in allSuites)
                AddItem(comboBox, cipherSuite, selected.Contains(cipherSuite));
        }

        private static void AddItem(ComboBox comboBox, string cipherSuite, bool isChecked)
        {
            var checkBox = new CheckBox
            {
                Content = cipherSuite,
                Tag = cipherSuite,
                Margin = new Thickness(6, 3, 10, 3),
                IsChecked = isChecked
            };
            checkBox.Checked += (_, __) => SaveSelection(comboBox);
            checkBox.Unchecked += (_, __) => SaveSelection(comboBox);
            comboBox.Items.Add(checkBox);
        }

        private static void SaveSelection(ComboBox comboBox)
        {
            if (comboBox == null)
                return;

            Global.setting.tcpClientSslCipherSuites = string.Join(":", GetCheckedCipherSuites(comboBox));
            UpdateDisplayText(comboBox);
            comboBox.Dispatcher.BeginInvoke(new Action(() => comboBox.IsDropDownOpen = true), DispatcherPriority.Background);
        }

        private static IEnumerable<string> GetCheckedCipherSuites(ComboBox comboBox)
        {
            return comboBox.Items.OfType<CheckBox>()
                .Where(item => item.IsChecked == true)
                .Select(item => item.Tag as string)
                .Where(value => !string.IsNullOrWhiteSpace(value));
        }

        private static void UpdateDisplayText(ComboBox comboBox)
        {
            if (comboBox == null)
                return;

            var selectedCount = SplitCipherSuites(Global.setting.tcpClientSslCipherSuites).Count();
            if (selectedCount == 0)
            {
                comboBox.Text = GetResourceText(comboBox, "TlsCipherSuitesDefault", "OpenSSL default");
                return;
            }

            var format = GetResourceText(comboBox, "TlsCipherSuitesSelectedFormat", "{0} selected");
            try
            {
                comboBox.Text = string.Format(format, selectedCount);
            }
            catch
            {
                comboBox.Text = selectedCount.ToString();
            }
        }

        private static string GetResourceText(ComboBox comboBox, string key, string fallback)
        {
            var getResourceText = comboBox.Tag as Func<string, string>;
            var value = getResourceText == null ? null : getResourceText(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static IEnumerable<string> GetAvailableCipherSuites()
        {
            lock (cacheLock)
            {
                if (cachedCipherSuites != null)
                    return cachedCipherSuites;

                var cipherSuites = new List<string>();
                var openSslCipherSuites = OpenSslCli.GetAvailableCipherSuites();
                AddDistinct(cipherSuites, openSslCipherSuites.Count > 0 ? openSslCipherSuites : FallbackCipherSuites);

                cachedCipherSuites = cipherSuites;
                return cachedCipherSuites;
            }
        }

        private static void AddDistinct(List<string> target, IEnumerable<string> source)
        {
            foreach (var value in source ?? Enumerable.Empty<string>())
            {
                var cipherSuite = (value ?? string.Empty).Trim();
                if (cipherSuite.Length == 0)
                    continue;
                if (target.Contains(cipherSuite, StringComparer.OrdinalIgnoreCase))
                    continue;

                target.Add(cipherSuite);
            }
        }

        private static IEnumerable<string> SplitCipherSuites(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ':', ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0);
        }
    }
}
