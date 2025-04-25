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
        log.Info($"Redis: {Redis.GetDatabase().IsConnected("test_key")}");
    }
}
