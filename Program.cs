using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Newtonsoft.Json.Linq;
using DotNetEnv;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Reflection.Metadata.Ecma335;
using System.Xml.Serialization;
using Microsoft.VisualBasic;

class Program
{
    private ulong NotifyChannelId;
    private List<string> _targetSteamIds;
    private string _steamApiKey;
    private readonly Dictionary<string, PlayerStatus> _lastStatuses = new Dictionary<string, PlayerStatus>();
    private DiscordSocketClient _client;
    private readonly HttpClient _httpClient = new HttpClient();
    private bool _isMonitoringStarted = false;

    static void Main(string[] args) => new Program().MainAsync().GetAwaiter().GetResult();

    public class PlayerStatus
    {
        public string Name { get; }
        public int State { get; set; }
        public GameInfo CurrentGame { get; set; }
        public List<GameInfo> GameHistory { get; } = new List<GameInfo>();

        public PlayerStatus(string name, int state, string gameName, string appId)
        {
            Name = name;
            State = state;
            CurrentGame = new GameInfo(gameName, appId);
            if (!string.IsNullOrEmpty(gameName)) GameHistory.Add(CurrentGame);
        }

        public bool IsHistoryExists(GameInfo game)
        {
            foreach (GameInfo info in GameHistory)
            {
                if (info.Equals(game)) return true;
            }
            return false;
        }
    }

        public struct GameInfo
    {
        public string Name { get; }
        public string AppId { get; }
        public GameInfo(string name, string appId)
        {
            Name = name;
            AppId = appId;
        }
        public override bool Equals(object obj) => obj is GameInfo other && Name == other.Name;
        public override int GetHashCode() => Name.GetHashCode();
    }

    public class SaesaeEmbedBuilder : EmbedBuilder
    {
        static public SaesaeEmbedBuilder InitSaesaeEmbedBuilder(string name, string avatarUrl)
        {
            var embed = new SaesaeEmbedBuilder();
            embed.WithColor(0xDB78E2)
                .WithAuthor(name, avatarUrl)
                .WithCurrentTimestamp();
            return embed;
        }

        public void AlertOnlineStatus(bool isOnline, string playerName)
        {
                string? strStatus = isOnline? "オンライン" : "オフライン";
                this.WithDescription($"**{playerName}**が{strStatus}になりました。");
        }

        public void StartGame(
            string appId,
            string playerName,
            string gameName)
        {
            this.WithGameImageUrl(appId);
            this.AddStartGameField(appId, playerName, gameName);
        }

        public void FinishGame(
            string appId, 
            string playerName,  
            string gameName)
        {
            this.AddFinishGameField(appId, playerName, gameName);
        }

        public void AddFinishGameField(
            string appId,
            string playerName,
            string gameName)
        {
            this.AddField($"**{gameName}**", $"**{playerName}**が[**{gameName}**](https://store.steampowered.com/app/{appId}/)を終了しました。");
        }

        private void WithGameImageUrl(string appId)
        {
            this.WithImageUrl($"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg");
        }

        private void AddStartGameField(
            string appId,
            string playerName,
            string gameName)
        {
            this.AddField($"**{gameName}**", $"**{playerName}**が[**{gameName}**](https://store.steampowered.com/app/{appId}/)を開始しました。");
        }
    }

    public async Task MainAsync()
    {
        DotNetEnv.Env.Load();

        string discordToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "";
        _steamApiKey = Environment.GetEnvironmentVariable("STEAM_API_KEY") ?? "";

        string idsRaw = Environment.GetEnvironmentVariable("STEAM_IDS") ?? "";
        var rawList = idsRaw.Split(',')
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        _targetSteamIds = new List<string>();

        foreach (var rawId in rawList)
        {
            string resolvedId = await GetSteamIdFromVanityUrl(rawId);
            if (!string.IsNullOrEmpty(resolvedId))
            {
                _targetSteamIds.Add(resolvedId);
                Console.WriteLine($"Resolved: {rawId} -> {resolvedId}");
            }
        }

        if (string.IsNullOrEmpty(discordToken) || string.IsNullOrEmpty(_steamApiKey))
        {
            Console.WriteLine("Error: DISCORD_TOKEN or STEAM_API_KEY is missing in .env");
            return;
        }

        NotifyChannelId = ulong.Parse(Environment.GetEnvironmentVariable("CHANNELID"));

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged
        });

        _client.Log += (log) => { Console.WriteLine(log.ToString()); return Task.CompletedTask; };
        _client.Ready += () => 
        {
            if (_isMonitoringStarted) return Task.CompletedTask;

            _isMonitoringStarted = true;
             _ = StartMonitoringLoop();
            return Task.CompletedTask;
        };

        await _client.LoginAsync(TokenType.Bot, discordToken);
        await _client.StartAsync();

        await Task.Delay(-1);
    }

    private async Task<string> GetSteamIdFromVanityUrl(string vanityUrl)
    {
        if (vanityUrl.Length == 17 && ulong.TryParse(vanityUrl, out _)) return vanityUrl;

        string url = $"https://api.steampowered.com/ISteamUser/ResolveVanityURL/v0001/?key={_steamApiKey}&vanityurl={vanityUrl}";
        
        var response = await _httpClient.GetStringAsync(url);
        var json = JObject.Parse(response);

        if ((int)json["response"]["success"] == 1)
        {
            return (string)json["response"]["steamid"];
        }
        
        Console.WriteLine($"Error: Failed to resolve vanity URL '{vanityUrl}'");
        return null;
    }

    private async Task StartMonitoringLoop()
    {
        IMessageChannel channel = null;
        ulong guildId = ulong.Parse(Environment.GetEnvironmentVariable("GUILD"));
 
        while (channel == null)
        {
            var guild = _client.GetGuild(guildId);
            if (guild != null)
            {
                channel = guild.GetTextChannel(NotifyChannelId);
            }
            await OnReady();
            if (channel == null)
            {
                Console.WriteLine($"Channel {NotifyChannelId} not found. Retrying in 5 seconds...");
                await Task.Delay(5000);
                continue;
            }
        }

        Console.WriteLine("Channel connected. Monitoring started...");

        while (true)
        {
            try { await PollSteamApiAsync(channel); }
            catch (Exception ex) { Console.WriteLine($"API Error: {ex.Message}"); }
            
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task OnReady()
    {
        foreach (var guild in _client.Guilds)
        {
            Console.WriteLine($"Joined Guild: {guild.Name} (ID: {guild.Id})");
            foreach (var ch in guild.TextChannels)
            {
                Console.WriteLine($"  - Channel: {ch.Name} (ID: {ch.Id})");
            }
        }
    }

    private async Task PollSteamApiAsync(IMessageChannel channel)
    {
        if (_targetSteamIds.Count == 0) return;

        string ids = string.Join(",", _targetSteamIds);
        string url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key={_steamApiKey}&steamids={ids}";
        
        var response = await _httpClient.GetStringAsync(url);
        var players = JObject.Parse(response)["response"]["players"];

        foreach (var player in players)
        {
            string steamId = (string)player["steamid"];
            string name = (string)player["personaname"];
            string currentGame = (string)player["gameextrainfo"] ?? "";
            string appId = (string)player["gameid"] ?? "";
            string avatarUrl = (string)player["avatarfull"];
            int currentState = (int)player["personastate"];
            
            var embed = SaesaeEmbedBuilder.InitSaesaeEmbedBuilder(name, avatarUrl);

            var currentGameInfo = new GameInfo(currentGame, appId);

            if (!_lastStatuses.ContainsKey(steamId))
            {
                _lastStatuses[steamId] = new PlayerStatus(name, currentState, currentGame, appId);
                continue;
            }

            var status = _lastStatuses[steamId];

            bool wasOnline = status.State > 0;
            bool isOnline = currentState > 0;

            if (isOnline != wasOnline)
            {
                embed.AlertOnlineStatus(isOnline, name);
                status.State = currentState;
                var emb = embed.Build();
                await channel.SendMessageAsync(embed: emb);
                continue;
            }

            var currentInfo = new GameInfo(currentGame, appId);
            
            if (!currentInfo.Equals(status.CurrentGame))
            {
                if (!string.IsNullOrEmpty(currentGame))
                {
                    if (status.IsHistoryExists(currentGameInfo))
                    {
                        if (!string.IsNullOrEmpty(status.CurrentGame.Name))
                        {
                            embed.FinishGame(appId, name, status.CurrentGame.Name);
                            status.GameHistory.Remove(status.CurrentGame);
                        }
                    }
                    else
                    {
                        embed.StartGame(appId, name, currentGame);
                        status.GameHistory.Add(currentInfo);
                    }
                }
                else
                {
                    foreach (var game in status.GameHistory.ToList())
                    {
                        embed.FinishGame(game.AppId, name, game.Name);
                        await channel.SendMessageAsync(embed: embed.Build());
                    }
                    status.GameHistory.Clear();
                    continue;
                }

                status.CurrentGame = currentGameInfo;
                var emb = embed.Build();
                await channel.SendMessageAsync(embed: emb);
                
            }
        }
    }
}


