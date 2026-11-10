using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ProbeLab;

internal static class BrowserProbe
{
    private static readonly string[] FieldIds = { "input", "textarea", "password", "readonly", "editable", "transform", "controlled", "autocomplete" };

    public static void Run(Report r)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        // PROBELAB_BROWSER=chrome 時優先使用 Chrome。
        var preferChrome = Environment.GetEnvironmentVariable("PROBELAB_BROWSER")?.Equals("chrome", StringComparison.OrdinalIgnoreCase) == true;
        var exe = (preferChrome ? candidates.Where(c => c.Contains("chrome.exe")) : candidates).FirstOrDefault(File.Exists);
        if (exe == null)
        {
            r.Add("略過", new { reason = "找不到 Edge 或 Chrome", searchedPaths = candidates }, null, "本機須已安裝瀏覽器；工具不安裝任何東西");
            return;
        }
        if (!NetworkSafety.RequireOffline(r)) return;
        var fixture = Path.Combine(Paths.Root, "fixtures", "browser-fixture.html");
        if (!File.Exists(fixture)) throw new FileNotFoundException("找不到內建 fixture", fixture);
        var uri = new Uri(fixture).AbsoluteUri;
        var profile = Path.Combine(Paths.Temp(), "browser-profile");
        Directory.CreateDirectory(profile);
        var port = RandomPort();
        var args = new[]
        {
            "--user-data-dir=" + profile, "--remote-debugging-port=" + port, "--remote-debugging-address=127.0.0.1",
            "--no-first-run", "--no-default-browser-check", "--enable-automation", "--disable-background-networking", "--disable-component-update",
            "--disable-sync", "--disable-extensions", "--disable-default-apps", "--disable-domain-reliability",
            "--safebrowsing-disable-auto-update", "--disable-features=OptimizationHints,MediaRouter,Translate,msEdgeSidebarV2,msEdgeShoppingAssistant",
            // The CDP HTTP server is deliberately not a forwarding proxy: proxy requests cannot leave loopback.
            "--proxy-server=http://127.0.0.1:" + port, "--proxy-bypass-list=<-loopback>", "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
            "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1", "--new-window", uri
        };
        using var launched = Launcher.StartShell(exe, args);
        // Edge 可能讓最初啟動的行程立即結束、改由另一個主程序擁有視窗。
        // 以「類別 Chrome_WidgetWin_1 且標題含 fixture 標題」的視窗找出真正的瀏覽器主程序 PID。
        var (foundHwnd, resolved) = WaitForBrowser(launched, "ProbeLab browser fixture");
        var process = resolved;
        var target = new OwnedTarget(process, r, browser: true);
        try
        {
            var hwnd = foundHwnd;
            r.Add("啟動隔離瀏覽器", new { pid = process.Id, hwnd = (long)hwnd, profile, port, url = uri }, hwnd != 0, "全新 profile、loopback CDP 與可識別主程序視窗");
            if (hwnd == 0) return;
            using var cdp = Cdp.Connect(port, uri);
            cdp.VerifyProfile(profile);
            cdp.BlockNetwork();
            if (!target.Activate(hwnd))
            {
                r.Add("前景安全檢查", new { reason = "無法將私有瀏覽器置於前景；拒絕輸入" }, false, "主程序 PID 與前景 PID 必須一致");
                return;
            }
            r.Add("CDP fixture 識別", cdp.Eval("({url:location.href,title:document.title,fieldCount:document.querySelectorAll('input,textarea,[contenteditable]').length})"), true,
                "僅連線到與內建 file URL 完全相符的頁面");
            foreach (var id in FieldIds)
            {
                if (!target.Guard(hwnd, "CDP focus " + id)) return;
                cdp.Eval("window.probeReset(" + J(id) + ")");
                Thread.Sleep(120);
                ClickField(cdp, target, hwnd, id, r);
                var initial = State(cdp, id);
                var initialUia = UiaProbe.BrowserField(hwnd, id,target);
                r.Add("初始 DOM/UIA 比較：" + id, new { dom = initial, uia = initialUia, comparison = Compare(initial, initialUia) }, null,
                    "DOM 為真值；逐欄比較 ValuePattern/TextPattern 的全文與 UTF-16 selection（密碼不要求 UIA 讀值）");
                if (id == "readonly")
                {
                    var sent = target.Input(hwnd, Native.Unicode("Z"), "readonly unicode");
                    Thread.Sleep(120);
                    var actual = State(cdp, id);
                    r.Add("唯讀拒絕輸入", new { sent, dom = actual, events = Events(cdp) }, actual.GetProperty("value").GetString() == "fixture readonly", "readonly 值維持 fixture readonly");
                    continue;
                }
                var input = "Ab中😀123456789";
                var count = target.Input(hwnd, Native.Unicode(input), id + " Unicode");
                Thread.Sleep(180);
                var state = State(cdp, id);
                var uia = UiaProbe.BrowserField(hwnd, id,target);
                var expected = id == "transform" ? input.ToUpperInvariant()[..10] : input;
                r.Add("Unicode 輸入：" + id, new
                {
                    requested = input, utf16Units = input.Length, requestedInputEvents = input.Length * 2,
                    sendInputReturned = count, expected, dom = state, uia, comparison = Compare(state, uia), events = Events(cdp)
                }, state.GetProperty("value").GetString() == expected && count == input.Length * 2,
                    "SendInput 回傳完整事件數且 DOM 為預期字串；transform 依 fixture 規則轉大寫並截 UTF-16 前 10 單位");
            }

            foreach (var id in new[] { "input", "textarea", "editable", "controlled" })
            {
                if (!target.Guard(hwnd, "CDP reset for middle edit " + id)) return;
                cdp.Eval("window.probeReset(" + J(id) + ")");
                ClickField(cdp, target, hwnd, id, r);
                var first = target.Input(hwnd, Native.Unicode("Goagle"), "Goagle " + id);
                Thread.Sleep(100);
                if (!target.Guard(hwnd, "CDP select middle " + id)) return;
                cdp.Eval(id == "editable"
                    ? "(()=>{let e=document.getElementById('editable');let r=document.createRange();r.setStart(e.firstChild,2);r.setEnd(e.firstChild,3);let s=getSelection();s.removeAllRanges();s.addRange(r);return true})()"
                    : "document.getElementById(" + J(id) + ").setSelectionRange(2,3)");
                var before = State(cdp, id);
                var uiaBefore = UiaProbe.BrowserField(hwnd, id,target);
                var replacement = target.Input(hwnd, Native.Unicode("o"), "middle replace " + id);
                Thread.Sleep(150);
                var after = State(cdp, id);
                r.Add("中間最小修改：" + id, new { initialSendReturned = first, before, uiaBefore, replacementSendReturned = replacement, after, uiaAfter = UiaProbe.BrowserField(hwnd, id,target), events = Events(cdp) },
                    after.GetProperty("value").GetString() == "Google" && replacement == 2, "僅選取 UTF-16 [2,3)，送一個 o，DOM 得到 Google");
            }

            // 不用 JS 設選取，改以 UIA TextPattern.Select 與鍵盤（Home→→→Shift+→）兩種方式選中間的 a，再輸入 o。
            foreach (var id in new[] { "input", "textarea", "editable", "controlled" })
            {
                foreach (var method in new[] { "uia", "keys" })
                {
                    if (!target.Guard(hwnd, "select-method middle edit " + id)) return;
                    cdp.Eval("window.probeReset(" + J(id) + ")");
                    ClickField(cdp, target, hwnd, id, r);
                    target.Input(hwnd, Native.Unicode("Goagle"), "Goagle " + method + " " + id);
                    Thread.Sleep(100);
                    object sel;
                    if (method == "uia") sel = UiaProbe.SelectRange(hwnd, id, 2, 1, target);
                    else
                    {
                        var keys = Native.Key(0x24).Concat(Native.Key(0x24, true))
                            .Concat(Native.Key(0x27)).Concat(Native.Key(0x27, true))
                            .Concat(Native.Key(0x27)).Concat(Native.Key(0x27, true))
                            .Concat(Native.Key(0x10)).Concat(Native.Key(0x27)).Concat(Native.Key(0x27, true)).Concat(Native.Key(0x10, true)).ToArray();
                        sel = new { keysSent = target.Input(hwnd, keys, "keys select middle " + id) };
                    }
                    Thread.Sleep(120);
                    var before = State(cdp, id);
                    var sent = target.Input(hwnd, Native.Unicode("o"), method + " replace " + id);
                    Thread.Sleep(150);
                    var after = State(cdp, id);
                    r.Add("中間修改(" + method + ")：" + id, new { sel, before, sent, after }, after.GetProperty("value").GetString() == "Google",
                        "不用 JS 設選取：以 " + method + " 選取 [2,3) 後輸入 o，DOM 得到 Google");
                }
            }

            // 網址列（Omnibox）實驗——使用者最常「搜尋 Google」的地方。只輸入、不按 Enter，不會導覽。
            {
                var info = UiaProbe.OmniboxInfo(hwnd, target, out var ox, out var oy);
                r.Add("網址列探查", info, null, "是否能以 UIA 找到網址列、其 Pattern 與位置");
                if (ox > 0)
                {
                    var click = target.Input(hwnd, Native.MouseClick(ox, oy), "omnibox click");
                    Thread.Sleep(300);
                    var selAll = UiaProbe.OmniboxSelect(hwnd, -1, 0, target);
                    var typed = target.Input(hwnd, Native.Unicode("Goagle"), "omnibox Goagle");
                    Thread.Sleep(400);
                    var afterType = UiaProbe.OmniboxRead(hwnd, target);
                    var sel = UiaProbe.OmniboxSelect(hwnd, 2, 1, target);
                    Thread.Sleep(120);
                    var selRead = UiaProbe.OmniboxRead(hwnd, target);
                    var replaced = target.Input(hwnd, Native.Unicode("o"), "omnibox replace o");
                    Thread.Sleep(400);
                    var afterReplace = UiaProbe.OmniboxRead(hwnd, target);
                    var okTyped = afterType.ToString()!.Contains("Goagle");
                    var okReplace = afterReplace.ToString()!.Contains("Google");
                    r.Add("網址列：輸入 Goagle→UIA 選 [2,3)→輸入 o",
                        new { click, selAll, typed, afterType, sel, selRead, replaced, afterReplace }, okTyped && okReplace,
                        "網址列值先為 Goagle，UIA 選取 [2,3) 並輸入 o 後為 Google（不按 Enter）");
                    target.Input(hwnd, Native.Key(0x1B).Concat(Native.Key(0x1B, true)).Concat(Native.Key(0x1B)).Concat(Native.Key(0x1B, true)).ToArray(), "omnibox Escape");
                }
            }

            if (!target.Guard(hwnd, "CDP autocomplete reset")) return;
            cdp.Eval("window.probeReset('autocomplete')");
            ClickField(cdp, target, hwnd, "autocomplete", r);
            var autoSend = target.Input(hwnd, Native.Unicode("goo"), "autocomplete goo");
            Thread.Sleep(150);
            var autoBefore = State(cdp, "autocomplete");
            var autoUia = UiaProbe.BrowserField(hwnd, "autocomplete",target);
            var back = target.Input(hwnd, Native.Key(0x08).Concat(Native.Key(0x08, true)).ToArray(), "autocomplete Backspace");
            Thread.Sleep(150);
            var autoAfter = State(cdp, "autocomplete");
            r.Add("自動完成選取與 Backspace", new { autoSend, before = autoBefore, uiaBefore = autoUia, backspaceSent = back, after = autoAfter, uiaAfter = UiaProbe.BrowserField(hwnd, "autocomplete",target), events = Events(cdp) },
                autoBefore.GetProperty("value").GetString() == "google.com" && autoBefore.GetProperty("start").GetInt32() == 3 && autoBefore.GetProperty("end").GetInt32() == 10 && autoAfter.GetProperty("value").GetString() == "goo",
                "輸入 goo 後選取 [3,10) 的 gle.com；一次 Backspace 後 DOM 為 goo");
        }
        finally
        {
            // This process was launched with a fresh profile. Never enumerate or terminate other browser processes.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            r.Add("私有瀏覽器清理", new { killedOnlyOwnedProcessTree = true, retainedProfile = profile }, true, "只終止本命令建立的程序樹；保留暫存 profile 供人工檢查");
        }
    }

    private static string J(string value) => JsonSerializer.Serialize(value);
    private static object Compare(JsonElement dom, object uia)
    {
        var view = JsonSerializer.SerializeToElement(uia);
        bool found = view.TryGetProperty("found", out var f) && f.GetBoolean();
        bool? valueMatches = null, textMatches = null, selectionMatches = null;
        if (found && view.TryGetProperty("data", out var data))
        {
            var expected = dom.GetProperty("value").GetString();
            if (data.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("fixtureText", out var v))
                valueMatches = v.GetString() == expected;
            if (data.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Object && text.TryGetProperty("fixtureText", out var t))
                textMatches = t.GetString() == expected;
            if (data.TryGetProperty("selection", out var selected) && selected.ValueKind == JsonValueKind.Array && selected.GetArrayLength() == 1 && dom.GetProperty("start").ValueKind == JsonValueKind.Number)
                selectionMatches = selected[0].GetProperty("startUtf16").GetInt32() == dom.GetProperty("start").GetInt32() && selected[0].GetProperty("endUtf16").GetInt32() == dom.GetProperty("end").GetInt32();
        }
        return new { found, valueMatchesDom = valueMatches, textMatchesDom = textMatches, selectionMatchesDom = selectionMatches, nullMeansUnsupportedOrUnavailable = true };
    }
    private static JsonElement State(Cdp cdp, string id) => cdp.Eval("window.probeState(" + J(id) + ")");
    private static JsonElement Events(Cdp cdp) => cdp.Eval("window.probeEvents");
    private static int RandomPort()
    {
        // The release-to-launch race is harmless: connect only after exact fixture URL validation.
        for (var i = 0; i < 20; i++)
        {
            var candidate = System.Security.Cryptography.RandomNumberGenerator.GetInt32(49152, 65536);
            var listener = new TcpListener(IPAddress.Loopback, candidate);
            try { listener.Start(); return candidate; }
            catch (SocketException) { }
            finally { listener.Stop(); }
        }
        throw new IOException("無法配置 loopback 高埠");
    }
    // 先用「真實滑鼠點擊」點到欄位中央，讓網頁取得鍵盤焦點（CDP 的 focus() 只設定 DOM 焦點，
    // 不保證視窗層級有鍵盤焦點）。座標由頁面推算：screenX/Y＋視窗外框偏移＋欄位矩形，再乘 devicePixelRatio。
    private static void ClickField(Cdp cdp, OwnedTarget target, nint hwnd, string id, Report r)
    {
        var pos = cdp.Eval("(()=>{const e=document.getElementById(" + J(id) + ");const b=e.getBoundingClientRect();const d=window.devicePixelRatio||1;" +
            "const side=(window.outerWidth-window.innerWidth)/2;" +
            "return {x:Math.round((window.screenX+side+b.left+b.width/2)*d),y:Math.round((window.screenY+(window.outerHeight-window.innerHeight)-side+b.top+b.height/2)*d)," +
            "dpr:d,hasFocus:document.hasFocus(),activeId:(document.activeElement||{}).id||null}})()");
        var x = pos.GetProperty("x").GetInt32();
        var y = pos.GetProperty("y").GetInt32();
        var sent = target.Input(hwnd, Native.MouseClick(x, y), "browser click " + id);
        Thread.Sleep(200);
        var after = cdp.Eval("({hasFocus:document.hasFocus(),activeId:(document.activeElement||{}).id||null})");
        r.Add("真實點擊欄位：" + id, new { pos, sent, after }, null, "點擊前後 document.hasFocus() 與作用中欄位 id；原始觀察，不預設結果");
    }

    private static (nint, Process) WaitForBrowser(Process launched, string title)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            nint found = 0; uint foundPid = 0;
            Native.EnumWindows((h, _) =>
            {
                var cls = new System.Text.StringBuilder(64); Native.GetClassName(h, cls, 64);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                var text = new System.Text.StringBuilder(256); Native.GetWindowText(h, text, 256);
                if (!text.ToString().Contains(title)) return true;
                Native.GetWindowThreadProcessId(h, out foundPid); found = h; return false;
            }, 0);
            if (found != 0) return (found, Process.GetProcessById((int)foundPid));
            Thread.Sleep(150);
        }
        return (0, launched);
    }

    private static nint WaitForWindow(Process p)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 15000)
        {
            if (p.HasExited) return 0;
            p.Refresh();
            if (p.MainWindowHandle != 0) return p.MainWindowHandle;
            Thread.Sleep(100);
        }
        return 0;
    }

    private sealed class Cdp : IDisposable
    {
        private readonly ClientWebSocket socket = new();
        private int nextId;
        readonly string fixtureUrl;
        private Cdp(Uri uri,string fixtureUrl)
        {
            this.fixtureUrl=fixtureUrl;
            socket.Options.Proxy=null;
            using var cancel = new CancellationTokenSource(5000);
            socket.ConnectAsync(uri, cancel.Token).GetAwaiter().GetResult();
        }
        public static Cdp Connect(int port, string fixtureUrl)
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000)
            {
                try
                {
                    var json = http.GetStringAsync("http://127.0.0.1:" + port + "/json/list").GetAwaiter().GetResult();
                    using var doc = JsonDocument.Parse(json);
                    foreach (var tab in doc.RootElement.EnumerateArray())
                    {
                        if (tab.GetProperty("type").GetString() != "page" || tab.GetProperty("url").GetString() != fixtureUrl) continue;
                        var ws = new Uri(tab.GetProperty("webSocketDebuggerUrl").GetString()!);
                        if (ws.Scheme != "ws" || ws.Host != "127.0.0.1" || ws.Port != port) throw new IOException("拒絕非 loopback CDP endpoint");
                        return new Cdp(ws,fixtureUrl);
                    }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
                Thread.Sleep(100);
            }
            throw new TimeoutException("找不到精確符合內建 fixture URL 的 CDP 頁面");
        }
        public JsonElement Eval(string expression)
        {
            expression="(()=>{if(location.href!=="+J(fixtureUrl)+")throw new Error('Refused non-fixture page');return ("+expression+");})()";
            var result = Call("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
            if (result.TryGetProperty("exceptionDetails", out var exception)) throw new IOException("fixture JavaScript error: " + exception);
            var remote = result.GetProperty("result");
            if (!remote.TryGetProperty("value", out var value)) return JsonSerializer.SerializeToElement(new { type = remote.GetProperty("type").GetString() });
            // SendInput 以 VK_PACKET 逐個 UTF-16 單位送入，網頁的 input 事件 data 會出現「孤立代理字元」，
            // System.Text.Json 無法重新序列化它們。這裡把孤立代理字元改成 <U+XXXX> 文字，保留觀察價值又能存檔。
            var text = value.GetRawText();
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\\u(d[89ab][0-9a-f]{2})(?!\\ud[c-f][0-9a-f]{2})", m => "<U+" + m.Groups[1].Value.ToUpperInvariant() + ">", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\\ud[89ab][0-9a-f]{2})\\u(d[c-f][0-9a-f]{2})", m => "<U+" + m.Groups[1].Value.ToUpperInvariant() + ">", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            using var sanitized = JsonDocument.Parse(text);
            return sanitized.RootElement.Clone();
        }
        public void VerifyProfile(string profile)
        {
            var commandLine = Call("Browser.getBrowserCommandLine", new { });
            var args = commandLine.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()).ToArray();
            if (!args.Contains("--user-data-dir=" + profile, StringComparer.OrdinalIgnoreCase))
                throw new IOException("CDP browser 未證明使用本命令的新建 profile；拒絕所有變更");
        }
        public void BlockNetwork()
        {
            Call("Network.enable", new { });
            Call("Network.setBlockedURLs", new { urls = new[] { "http://*", "https://*", "ws://*", "wss://*" } });
            Call("Network.emulateNetworkConditions", new { offline = true, latency = 0, downloadThroughput = 0, uploadThroughput = 0 });
        }
        private JsonElement Call(string method, object parameters)
        {
            var id = ++nextId;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            using var cancel = new CancellationTokenSource(5000);
            socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancel.Token).GetAwaiter().GetResult();
            while (true)
            {
                using var stream = new MemoryStream();
                var buffer = new byte[32768];
                WebSocketReceiveResult packet;
                do
                {
                    packet = socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel.Token).GetAwaiter().GetResult();
                    if (packet.MessageType == WebSocketMessageType.Close) throw new IOException("CDP 已關閉");
                    stream.Write(buffer, 0, packet.Count);
                    if (stream.Length > 8 * 1024 * 1024) throw new IOException("CDP 回覆超過 8 MiB");
                } while (!packet.EndOfMessage);
                using var doc = JsonDocument.Parse(stream.ToArray());
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var replyId) || replyId.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out var error)) throw new IOException("CDP error: " + error);
                return root.GetProperty("result").Clone();
            }
        }
        public void Dispose() => socket.Dispose();
    }
}
