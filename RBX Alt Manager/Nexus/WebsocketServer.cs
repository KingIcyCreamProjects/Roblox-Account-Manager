using RBX_Alt_Manager.Forms;
using System;
using System.Linq;
using System.Threading;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace RBX_Alt_Manager.Nexus
{
    public class WebsocketServer : WebSocketBehavior
    {
        private static int _num = 0;

        private string _name;
        private string _prefix;

        public WebsocketServer() : this("anon#")
        {
        }

        public WebsocketServer(string prefix) => _prefix = prefix;

        private string getName() => Context.QueryString["name"] ?? (_prefix + getNum());
        private int getNum() => Interlocked.Increment(ref _num);

        // Constant-time string compare so a network attacker can't time-probe the shared secret.
        private static bool FixedTimeEquals(string a, string b)
        {
            byte[] x = System.Text.Encoding.UTF8.GetBytes(a ?? "");
            byte[] y = System.Text.Encoding.UTF8.GetBytes(b ?? "");

            int diff = x.Length ^ y.Length;
            for (int i = 0; i < y.Length; i++) diff |= (i < x.Length ? x[i] : (byte)0) ^ y[i];

            return diff == 0;
        }

        protected override void OnOpen()
        {
            // The in-game Lua client sends no Origin; a browser page attempting to drive this control socket does.
            if (!string.IsNullOrEmpty(Context.Origin)) { Context.WebSocket.Close(); return; }

            // A non-loopback client (only reachable when AllowExternalConnections binds every interface) must present
            // the per-session shared secret. Loopback stays open — "knows a username" was the ONLY barrier before,
            // which is no barrier at all for a LAN/internet peer.
            if (!Context.IsLocal)
            {
                string Provided = Context.QueryString["token"] ?? "";
                if (string.IsNullOrEmpty(AccountControl.NexusToken) || !FixedTimeEquals(Provided, AccountControl.NexusToken)) { Context.WebSocket.Close(); return; }
            }

            if (string.IsNullOrEmpty(Context.QueryString["name"]) || string.IsNullOrEmpty(Context.QueryString["id"])) { Context.WebSocket.Close(); return; }

            string jobID = string.IsNullOrEmpty(Context.QueryString["jobId"]) ? "UNKNOWN" : Context.QueryString["jobId"];

            long.TryParse(Context.QueryString["id"], out long UserId);

            _name = getName();

            ControlledAccount Account = AccountControl.Instance.Accounts.FirstOrDefault(x => x.Username == Context.QueryString["name"]);

            if (Account != null)
            {
                Account.Connect(Context);
                Account.InGameJobId = jobID;
                AccountControl.Instance.AccountsView.RefreshObject(Account);
            }
            else
                Context.WebSocket.Close();
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (AccountControl.Instance.ContextList.TryGetValue(Context, out ControlledAccount account))
                account.HandleMessage(e.Data);
        }

        protected override void OnClose(CloseEventArgs e)
        {
            if (AccountControl.Instance.ContextList.TryGetValue(Context, out ControlledAccount Account))
                // Only flip the account Offline if this socket is still its current one. A late OnClose
                // from a superseded socket (duplicate-name reconnect/teleport) must not disconnect an
                // account that has already reconnected on a newer socket — just drop the stale entry.
                // CloseIfCurrent does the check-and-teardown atomically under the account's lock so a
                // concurrent Connect() can't swap Context between the check and Disconnect().
                Account.CloseIfCurrent(Context);
        }

        protected override void OnError(ErrorEventArgs e) => Program.Logger.Error($"WebsocketServer Error {_name}: {e.Message} {e.Exception}");
    }
}