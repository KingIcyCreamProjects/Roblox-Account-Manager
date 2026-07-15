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

        protected override void OnOpen()
        {
            // The in-game Lua client sends no Origin; a browser page attempting to drive this control socket does.
            if (!string.IsNullOrEmpty(Context.Origin)) { Context.WebSocket.Close(); return; }

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
            {
                // Only flip the account Offline if this socket is still its current one. A late OnClose
                // from a superseded socket (duplicate-name reconnect/teleport) must not disconnect an
                // account that has already reconnected on a newer socket — just drop the stale entry.
                if (Account.Context == Context)
                    Account.Disconnect();
                else
                    AccountControl.Instance.ContextList.TryRemove(Context, out _);
            }
        }

        protected override void OnError(ErrorEventArgs e) => Program.Logger.Error($"WebsocketServer Error {_name}: {e.Message} {e.Exception}");
    }
}