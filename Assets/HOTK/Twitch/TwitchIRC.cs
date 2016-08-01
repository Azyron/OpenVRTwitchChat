using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Net;
using Newtonsoft.Json;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Net.Cache;

public class TwitchIRC : MonoBehaviour
{
    public const string DefaultChatNameColor = "#FFFFFFFF";
    public string Oauth;
    public string NickName;
    public string ChannelName;
    private const string Server = "irc.twitch.tv";
    private const int Port = 6667;

    //event(buffer).
    public class MsgEvent : UnityEngine.Events.UnityEvent<string> { }
    public MsgEvent MessageRecievedEvent = new MsgEvent();

    private string _buffer = string.Empty;
    private bool _stopThreads;
    private readonly Queue<string> _commandQueue = new Queue<string>();
    private readonly List<string> _recievedMsgs = new List<string>();
    private System.Threading.Thread _inProc, _outProc;

    private static readonly Dictionary<string, string> UserColors = new Dictionary<string, string>();
    private static System.Random Random = new System.Random();

    private bool _connected;
    private bool _loggedin;

    private String twitchApiUrl = null;
    private String lastNewFollower = null;


    public void StartIRC()
    {
        twitchApiUrl = "https://api.twitch.tv/kraken/channels/CmdrKuixote/follows";

        _stopThreads = false;
        _commandQueue.Clear();
        _recievedMsgs.Clear();
        var sock = new System.Net.Sockets.TcpClient();
        sock.Connect(Server, Port);
        if (!sock.Connected)
        {
            ToNotice("System", "Failed to connect!", NoticeColor.Red);
            return;
        }
        var networkStream = sock.GetStream();
        var input = new System.IO.StreamReader(networkStream);
        var output = new System.IO.StreamWriter(networkStream);

        _loggedin = false;
        _connected = false;
        //Send PASS & NICK.
        output.WriteLine("PASS " + Oauth);
        output.WriteLine("NICK " + NickName.ToLower());
        output.Flush();

        //output proc
        _outProc = new System.Threading.Thread(() => IRCOutputProcedure(output));
        _outProc.Start();
        //input proc
        _inProc = new System.Threading.Thread(() => IRCInputProcedure(input, networkStream));
        _inProc.Start();

        CancelInvoke("CheckConnection");
        Invoke("CheckConnection", 20f);
    }

    private void CheckConnection()
    {
        if (_stopThreads) return;
        lock (_recievedMsgs)
        {
            if (!_loggedin)
            {
                _recievedMsgs.Add(ToNotice("System", "Should be logged in by now.. are the username and oauth correct?", NoticeColor.Yellow));
            }
            else if (!_connected)
            {
                _recievedMsgs.Add(ToNotice("System", "Should be connected by now.. is the channel name correct?", NoticeColor.Yellow));
            }
        }
    }

    public class User
    {
        public long _id { get; set; }
        public string name { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public Dictionary<string, string> _links { get; set; }
        public string display_name { get; set; }
        public object logo { get; set; }
        public object bio { get; set; }
        public string type { get; set; }
    }

    public class Follow
    {
        public DateTime created_at { get; set; }
        public Dictionary<string, string> _links { get; set; }
        public bool notifications { get; set; }
        public User user { get; set; }
    }

    public class RootObject
    {
        public RootObject()
        {
            this.follows = new List<Follow>();
        }
        public List<Follow> follows { get; set; }
        public int _total { get; set; }
        public Dictionary<string, string> _links { get; set; }
    }


    public bool MyRemoteCertificateValidationCallback(System.Object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        bool isOk = true;
        // If there are errors in the certificate chain, look at each error to determine the cause.
        if (sslPolicyErrors != SslPolicyErrors.None)
        {
            for (int i = 0; i < chain.ChainStatus.Length; i++)
            {
                if (chain.ChainStatus[i].Status != X509ChainStatusFlags.RevocationStatusUnknown)
                {
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                    chain.ChainPolicy.UrlRetrievalTimeout = new TimeSpan(0, 1, 0);
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
                    bool chainIsValid = chain.Build((X509Certificate2)certificate);
                    if (!chainIsValid)
                    {
                        isOk = false;
                    }
                }
            }
        }
        return isOk;
    }

    private void IRCInputProcedure(System.IO.TextReader input, System.Net.Sockets.NetworkStream networkStream)
    {
        long apiSleepTime = 0;
        ServicePointManager.ServerCertificateValidationCallback = MyRemoteCertificateValidationCallback;

        WebClient wc = new WebClient();

        while (!_stopThreads)
        {
            try
            {
                if (apiSleepTime > 10000)
                {
                    string json =wc.DownloadString(twitchApiUrl);
                    var result = JsonConvert.DeserializeObject<RootObject>(json);
                    var latestFollow = result.follows.Aggregate((Follow)null, (f1, f2) => (f1 == null || f2 == null ? f1 ?? f2 : f2.created_at > f1.created_at ? f2 : f1));

                    if (lastNewFollower == null)
                    {
                        lastNewFollower = latestFollow.user.display_name;
                    }
                    else if (lastNewFollower.Equals(latestFollow.user.display_name) == false)
                    {
                        lock (_recievedMsgs)
                        {
                            _recievedMsgs.Add(ToTwitchNotice("New follower: \"" + latestFollow.user.display_name + "\"."));
                            lastNewFollower = latestFollow.user.display_name;
                        }
                    }

                    apiSleepTime = 0;
                }

                if (!networkStream.DataAvailable)
                {
                    Thread.Sleep(20);
                    apiSleepTime += 20;
                    continue;
                }

                _buffer = input.ReadLine();
                if (_buffer == null) continue;
                //Debug.Log(_buffer);

                string[] tokens;
                string message;
                if (_buffer.StartsWith("@"))
                {
                    var split = _buffer.IndexOf(' ');
                    var userstate = _buffer.Substring(0, split);
                    message = _buffer.Substring(split + 1);
                    tokens = message.Split(' ');

                    var username = tokens[0].Split('!')[0].Substring(1);
                    var keys = userstate.Split(';');

                    foreach (var k in keys)
                    {
                        if (k.StartsWith("color="))
                        {
                            if (GetUserColor(username) != DefaultChatNameColor) continue;
                            var color = (k != "color=") ? k.Substring(7) : null;
                            if (string.IsNullOrEmpty(color))
                            {
                                var r = Mathf.Max(0.25f, Random.Next(0, 100)/100f);
                                var g = Mathf.Max(0.25f, Random.Next(0, 100)/100f);
                                var b = Mathf.Max(0.25f, Random.Next(0, 100)/100f);
                                color = ColorToHex(new Color(r, g, b));
                            }
                            lock (UserColors)
                            {
                                UserColors.Add(username, color);
                            }
                        }
                    }
                }
                else
                {
                    message = _buffer;
                    tokens = _buffer.Split(' ');
                }

                switch (tokens[1])
                {
                    case "PRIVMSG":
                    case "NOTICE":
                        lock (_recievedMsgs)
                        {
                            _recievedMsgs.Add(message);
                        }
                        break;
                    case "JOIN":
                        lock (_recievedMsgs)
                        {
                            _recievedMsgs.Add(ToTwitchNotice(string.Format("Connected to {0}!", tokens[2])));
                            _connected = true;
                        }
                        break;
                    case "001":
                        lock (_recievedMsgs)
                        {
                            _recievedMsgs.Add(ToTwitchNotice("Logged in! Connecting to chat.."));
                            _loggedin = true;
                        }
                        SendCommand("CAP REQ :twitch.tv/tags");
                        SendCommand("CAP REQ :twitch.tv/commands");
                        SendCommand("JOIN #" + ChannelName);
                        break;
                    case "CAP":
                        lock (_recievedMsgs)
                        {
                            _recievedMsgs.Add(ToTwitchNotice("Acknowledging Client Capabilities!"));
                        }
                        break;
                    case "USERSTATE":
                        break;
                    default:
                        if (_buffer.StartsWith("PING "))
                        {
                            SendCommand(_buffer.Replace("PING", "PONG"));
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                lock (_recievedMsgs)
                {
                    _recievedMsgs.Add(ToNotice("EXCEPTION", e.ToString(), NoticeColor.Red));
                }
            }
        }
    }

    private void IRCOutputProcedure(System.IO.TextWriter output)
    {
        System.Diagnostics.Stopwatch stopWatch = new System.Diagnostics.Stopwatch();
        stopWatch.Start();
        while (!_stopThreads)
        {
            lock (_commandQueue)
            {
                if (_commandQueue.Count <= 0)
                {
                    Thread.Sleep(20);
                    continue;
                }
                // https://github.com/justintv/Twitch-API/blob/master/IRC.md#command--message-limit
                //has enough time passed since we last sent a message/command?
                if (stopWatch.ElapsedMilliseconds <= 1750)
                {
                    Thread.Sleep(20);
                    continue;
                }
                //send msg.
                output.WriteLine(_commandQueue.Peek());
                output.Flush();
                //remove msg from queue.
                _commandQueue.Dequeue();
                //restart stopwatch.
                stopWatch.Reset();
                stopWatch.Start();
            }
        }
    }

    public void SendCommand(string cmd)
    {
        lock (_commandQueue)
        {
            _commandQueue.Enqueue(cmd);
        }
    }
    public void SendMsg(string msg)
    {
        lock (_commandQueue)
        {
            _commandQueue.Enqueue("PRIVMSG #" + ChannelName + " :" + msg);
        }
    }
    public void OnEnable()
    {
        _stopThreads = false;
    }
    public void OnDisable()
    {
        _stopThreads = true;
        CancelInvoke("CheckConnection");
    }
    public void OnDestroy()
    {
        _stopThreads = true;
        CancelInvoke("CheckConnection");
    }
    public void Update()
    {
        lock (_recievedMsgs)
        {
            if (_recievedMsgs.Count <= 0) return;
            foreach (var t in _recievedMsgs)
            {
                MessageRecievedEvent.Invoke(t);
            }
            _recievedMsgs.Clear();
        }
    }

    public static string ToTwitchNotice(string msgIn, NoticeColor colorEnum = NoticeColor.Green)
    {
        return ToNotice("Twitch", msgIn, colorEnum);
    }

    public static string ToNotice(string nickname, string msgIn, NoticeColor colorEnum = NoticeColor.Green)
    {
        return string.Format(":{0} NOTICE {1} :{2}", nickname, NoticeColorToString(colorEnum), msgIn);
    }

    public static string GetUserColor(string username)
    {
        lock (UserColors)
        {
            string hex;
            return UserColors.TryGetValue(username, out hex) ? hex : DefaultChatNameColor;
        }
    }

    public static string ColorToHex(Color32 color)
    {
        return color.r.ToString("X2") + color.g.ToString("X2") + color.b.ToString("X2");
    }

    public static Color HexToColor(string hex)
    {
        hex = hex.Replace("0x", "");//in case the string is formatted 0xFFFFFF
        hex = hex.Replace("#", "");//in case the string is formatted #FFFFFF
        byte a = 255;//assume fully visible unless specified in hex
        var r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        var g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        var b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        //Only use alpha if the string has enough characters
        if (hex.Length == 8)
            a = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        return new Color32(r, g, b, a);
    }


    public static string NoticeColorToString(NoticeColor colorEnum)
    {
        switch (colorEnum)
        {
            case NoticeColor.Green:
                return "*System-Green";
            case NoticeColor.Red:
                return "*System-Red";
            case NoticeColor.Blue:
                return "*System-Blue";
            case NoticeColor.Yellow:
                return "*System-Yellow";
            case NoticeColor.Purple:
                return "*System-Purple";
            case NoticeColor.White:
                return "*System-White";
            default:
                throw new ArgumentOutOfRangeException("colorEnum", colorEnum, null);
        }
    }

    public enum NoticeColor
    {
        Green,
        Red,
        Blue,
        Yellow,
        Purple,
        White
    }
}
