using System;
using log4net;
using StackExchange.Redis;

namespace ACE.Server.Managers;

public static class RedisManager
{
    private static readonly ILog log = LogManager.GetLogger(nameof(RedisManager));
    public static readonly ConnectionMultiplexer Redis;

    static RedisManager()
    {
        Redis = ConnectionMultiplexer.Connect(Common.ConfigManager.Config.Redis.Host);
    }

    public static void Start()
    {
        var isConnected = Redis.GetDatabase().IsConnected("test_key");
        if (isConnected) log.Info("Connected to Redis.");
        else log.Error($"Could not connect to Redis at {Common.ConfigManager.Config.Redis.Host}!");
    }
}
