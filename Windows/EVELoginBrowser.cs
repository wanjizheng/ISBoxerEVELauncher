using ISBoxerEVELauncher.Extensions;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.Windows.Forms;
using ISBoxerEVELauncher.Web;
using Microsoft.Web.WebView2.Core;

namespace ISBoxerEVELauncher.Windows
{
    public partial class EVELoginBrowser : Form
    {
        // Launcher callback base URL — when navigation lands here, we have the auth code.
        private const string CallbackBase = "https://login.eveonline.com/launcher";

        /// <summary>
        /// Returns true if the WebView2 Runtime is installed and available on this machine.
        /// Uses registry detection so it does not require WebView2Loader.dll to be present.
        /// </summary>
        public static bool IsWebView2Available()
        {
            // First try: check registry for Evergreen WebView2 Runtime
            // GUID {F3017226-FE2A-4295-8BDF-00C3A9A7E4C5} is the WebView2 Runtime product GUID
            const string webView2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            string[] registryPaths = new[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + webView2Guid,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + webView2Guid,
                // Edge stable also ships WebView2 Runtime
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}",
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}",
            };

            foreach (string path in registryPaths)
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path))
                    {
                        if (key != null)
                        {
                            string pv = key.GetValue("pv") as string;
                            if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0")
                                return true;
                        }
                    }
                }
                catch { }
            }

            // Second try: call the API (works if WebView2Loader.dll is present in app dir)
            try
            {
                string version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
                return !string.IsNullOrEmpty(version);
            }
            catch { }

            return false;
        }

        public string strCurrentAddress { get; set; }

        // Legacy fields kept for compatibility with Response.cs WebRequestType branches
        public string strHTML_RequestVerificationToken { get; set; }
        public string strURL_RequestVerificationToken { get; set; }
        public string strHTML_VerficationCode { get; set; }
        public string strURL_VerficationCode { get; set; }

        // strURL_Result holds the full callback URL containing the auth code
        public string strHTML_Result { get; set; }
        public string strURL_Result { get; set; }

        public EVELoginBrowser()
        {
            InitializeComponent();
            Clearup();
            toolStripTextBox_Addressbar.Size = new Size(
                toolStrip_Main.Size.Width - toolStripButton_Refresh.Size.Width - 20,
                toolStripTextBox_Addressbar.Size.Height);

            // Wire up WebView2 events once the control is ready
            webBrowser_EVE.NavigationCompleted += WebBrowser_EVE_NavigationCompleted;
            webBrowser_EVE.NavigationStarting += WebBrowser_EVE_NavigationStarting;
            webBrowser_EVE.CoreWebView2InitializationCompleted += WebBrowser_EVE_CoreWebView2InitializationCompleted;
        }

        public void Clearup()
        {
            strCurrentAddress = "";
            strHTML_RequestVerificationToken = "";
            strURL_RequestVerificationToken = "";
            strHTML_VerficationCode = "";
            strURL_VerficationCode = "";
            strHTML_Result = "";
            strURL_Result = "";
        }

        private void WebBrowser_EVE_CoreWebView2InitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess) return;
            // Suppress new window popups — open them in same tab
            webBrowser_EVE.CoreWebView2.NewWindowRequested += (s, args) =>
            {
                args.Handled = true;
                webBrowser_EVE.CoreWebView2.Navigate(args.Uri);
            };
        }

        private void WebBrowser_EVE_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            this.InvokeOnUiThreadIfRequired(() => toolStripTextBox_Addressbar.Text = e.Uri);
            strCurrentAddress = e.Uri;

            // Intercept the launcher callback URL
            if (e.Uri.StartsWith(CallbackBase, StringComparison.OrdinalIgnoreCase)
                && e.Uri.Contains("code="))
            {
                e.Cancel = true;
                strHTML_Result = e.Uri;   // store URL as "body" — code is parsed from it later
                strURL_Result = e.Uri;
                this.InvokeOnUiThreadIfRequired(() => this.Close());
            }
        }

        private void WebBrowser_EVE_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (webBrowser_EVE.CoreWebView2 == null) return;
            string url = webBrowser_EVE.Source?.ToString() ?? "";
            this.InvokeOnUiThreadIfRequired(() => toolStripTextBox_Addressbar.Text = url);
            strCurrentAddress = url;

            // Auto-fill credentials on the EVE login page
            bool isLoginPage = url.IndexOf("login.eveonline.com", StringComparison.OrdinalIgnoreCase) >= 0
                            && !url.Contains("code=");

            if (isLoginPage && !string.IsNullOrEmpty(App.strUserName))
            {
                InjectAutoFillScriptAsync();
            }
        }

        private async void InjectAutoFillScriptAsync()
        {
            if (webBrowser_EVE.CoreWebView2 == null) return;

            // JSON-encode credentials so any special characters are safely escaped as JS string literals
            string userJs = Newtonsoft.Json.JsonConvert.SerializeObject(App.strUserName ?? "");
            string passJs = Newtonsoft.Json.JsonConvert.SerializeObject(App.strPassword ?? "");

            // Persistent autofill script:
            // - Uses MutationObserver so it works regardless of when React mounts the form
            // - Fills username and password in one synchronous pass to avoid React re-render races
            // - Dispatches the full event chain React/Vue/Angular expect: focus -> input -> change -> blur
            // - Re-checks values after a short delay; if they were wiped, refills before submitting
            string script = $@"(function() {{
    if (window.__eveAutoFillInstalled) return;
    window.__eveAutoFillInstalled = true;

    var USERNAME = {userJs};
    var PASSWORD = {passJs};
    var submitted = false;
    var observer = null;

    function setNativeVal(el, val) {{
        var proto = el.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
        var descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
        if (descriptor && descriptor.set) {{ descriptor.set.call(el, val); }} else {{ el.value = val; }}
        el.dispatchEvent(new Event('focus',  {{ bubbles: true }}));
        el.dispatchEvent(new Event('input',  {{ bubbles: true }}));
        el.dispatchEvent(new Event('change', {{ bubbles: true }}));
        el.dispatchEvent(new Event('blur',   {{ bubbles: true }}));
    }}

    function findUserInput() {{
        return document.querySelector('input[name=""UserName""]')
            || document.querySelector('input[name=""username""]')
            || document.querySelector('input[autocomplete=""username""]')
            || document.querySelector('input[autocomplete=""email""]')
            || document.querySelector('input[type=""email""]')
            || document.querySelector('input[id*=""ser""i]')
            || document.querySelector('form input[type=""text""]');
    }}
    function findPassInput() {{
        return document.querySelector('input[type=""password""]')
            || document.querySelector('input[name=""Password""]')
            || document.querySelector('input[name=""password""]')
            || document.querySelector('input[autocomplete=""current-password""]');
    }}
    function tryFill() {{
        if (submitted) return true;
        var u = findUserInput();
        var p = findPassInput();
        if (!u || !p) return false;

        // Pause observer while filling to avoid re-entry during React re-renders
        if (observer) observer.disconnect();

        // Fill both inputs in the same synchronous tick
        if (u.value !== USERNAME) setNativeVal(u, USERNAME);
        if (p.value !== PASSWORD) setNativeVal(p, PASSWORD);

        // Give React one frame to commit, then verify and submit
        setTimeout(function() {{
            if (submitted) return;
            var u2 = findUserInput(); var p2 = findPassInput();
            if (!u2 || !p2) return;
            if (u2.value !== USERNAME) setNativeVal(u2, USERNAME);
            if (p2.value !== PASSWORD) setNativeVal(p2, PASSWORD);

            submitted = true;

            // Strategy 1: press Enter on the password field (works regardless of button state)
            p2.focus();
            p2.dispatchEvent(new KeyboardEvent('keydown',  {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }}));
            p2.dispatchEvent(new KeyboardEvent('keypress', {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }}));
            p2.dispatchEvent(new KeyboardEvent('keyup',    {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }}));

            // Strategy 2: also try clicking submit button (in case Enter alone is not enough)
            setTimeout(function() {{
                var btn = document.querySelector('button[type=""submit""]')
                       || document.querySelector('input[type=""submit""]')
                       || document.querySelector('form button')
                       || document.querySelector('button');
                if (btn) {{
                    btn.removeAttribute('disabled');
                    btn.click();
                }}
            }}, 100);
        }}, 400);

        return true;
    }}

    // Try immediately
    tryFill();

    // Watch for the form to appear or React re-renders
    observer = new MutationObserver(function() {{ tryFill(); }});
    observer.observe(document.documentElement, {{ childList: true, subtree: true }});

    // Stop after 30s
    setTimeout(function() {{ observer.disconnect(); }}, 30000);
}})();";

            try
            {
                await webBrowser_EVE.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch { }
        }

        private void EVELoginBrowser_Resize(object sender, EventArgs e)
        {
            toolStripTextBox_Addressbar.Size = new Size(
                toolStrip_Main.Size.Width - toolStripButton_Refresh.Size.Width - 20,
                toolStripTextBox_Addressbar.Size.Height);
        }

        private void toolStripButton_Refresh_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(toolStripTextBox_Addressbar.Text))
                webBrowser_EVE.CoreWebView2?.Navigate(toolStripTextBox_Addressbar.Text);
        }
    }
}
