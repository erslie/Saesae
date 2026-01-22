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

class Program
{
    private ulong NotifyChannelId;
    private List<string> _targetSteamIds;
    private string _steamApiKey;
    private readonly Dictionary<string, PlayerStatus> _lastStatuses = new Dictionary<string, PlayerStatus>();
    private DiscordSocketClient _client;
    private readonly HttpClient _httpClient = new HttpClient();

    static void Main(string[] args) => new Program().MainAsync().GetAwaiter().GetResult();

public class PlayerStatus
{
    public string Name { get; }
    public int State { get; set; }
    public string CurrentGame { get; set; }
    public List<string> GameHistory { get; } = new List<string>();

    public PlayerStatus(string name, int state, string gameName)
    {
        Name = name;
        State = state;
        CurrentGame = gameName;
        if (!string.IsNullOrEmpty(gameName)) GameHistory.Add(gameName);
    }
}

    public async Task MainAsync()
    {
        DotNetEnv.Env.Load();

        string idsRaw = Environment.GetEnvironmentVariable("STEAM_IDS") ?? "";
        _targetSteamIds = idsRaw.Split(',')
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrEmpty(id)) 
            .ToList();

        string discordToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "";
        _steamApiKey = Environment.GetEnvironmentVariable("STEAM_API_KEY") ?? "";

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
        _client.Ready += () => { _ = StartMonitoringLoop(); return Task.CompletedTask; };

        await _client.LoginAsync(TokenType.Bot, discordToken);
        await _client.StartAsync();

        await Task.Delay(-1);
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
            
            var embed = new EmbedBuilder()
                .WithColor(0xDB78E2)
                .WithAuthor(name, avatarUrl)
                .WithCurrentTimestamp();

            if (!_lastStatuses.ContainsKey(steamId))
            {
                _lastStatuses[steamId] = new PlayerStatus(name, currentState, currentGame);
                continue;
            }

            var status = _lastStatuses[steamId];

            bool wasOnline = status.State > 0;
            bool isOnline = currentState > 0;

            if (isOnline != wasOnline)
            {
                string? strStatus = isOnline? "オンライン" : "オフライン";
                embed.WithDescription($"**{name}**が{strStatus}になりました。");
                status.State = currentState;
                var emb = embed.Build();
                await channel.SendMessageAsync(embed: emb);
                continue;
            }

            embed.WithImageUrl($"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg");
            
            if (currentGame != status.CurrentGame)
            {
                if (!string.IsNullOrEmpty(currentGame))
                {
                    if (status.GameHistory.Contains(currentGame))
                    {
                        if (!string.IsNullOrEmpty(status.CurrentGame))
                        {
                            embed.WithDescription($"**{name}**が**{status.CurrentGame}**を終了しました。");
                            status.GameHistory.Remove(status.CurrentGame);
                        }
                    }
                    else
                    {
                        embed.AddField($"**{currentGame}**", $"**{name}**が[**{currentGame}**](https://store.steampowerd.com/app/{appId}/)を開始しました。");
                        status.GameHistory.Add(currentGame);
                    }
                }
                else
                {
                    foreach (var game in status.GameHistory.ToList())
                    {
                        embed.WithTitle(game);
                        embed.WithDescription($"**{name}** が **{game}** を終了しました。");
                    }
                    status.GameHistory.Clear();
                }

                status.CurrentGame = currentGame;
                var emb = embed.Build();
                await channel.SendMessageAsync(embed: emb);
                
            }
        }
    }
}
