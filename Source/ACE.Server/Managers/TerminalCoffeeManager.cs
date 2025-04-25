using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using log4net;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Weenie = ACE.Database.Models.World.Weenie;

namespace ACE.Server.Managers;

public class CoffeeResponse
{
    public List<CoffeeProduct> data { get; set; }
}

public class CoffeeProduct
{
    public string id { get; set; }
    public string name { get; set; }
    public string description { get; set; }
    public List<Variant> variants { get; set; }
}

public class Variant
{
    public string name { get; set; }
    public int price { get; set; }
}

public class CoffeePurchaseJob
{
    public Guid id { get; set; }
    public string coffeeProductId { get; set; }
    public int retries { get; set; }
    public DateTime nextAttempt { get; set; }
}

public static class TerminalCoffeeManager
{
    private static readonly ILog log = LogManager.GetLogger(nameof(TerminalCoffeeManager));


    private const string RedisIncomingKey = "coffee.products:incoming";
    private const string RedisDetailsKey = "coffee.products:details";
    private const string RedisVendorKey = "coffee.products:vendor";
    private const string RedisCreatedKey = "coffee.products:created";
    private const string RedisProductToWeeniesKey = "coffee.products:product.to.weenie";
    private const string RedisWeeniesToProductKey = "coffee.products:weenie.to.product";
    private const string RedisOrderConversionQueueKey = "coffee.products:order-conversion-queue";
    private const uint BarkeepLienneVendorId = 694;

    private static DateTime _lastRunTime = DateTime.MinValue;
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(12);
    private static readonly TerminalWebClient WebClient = new();

    public static void Tick()
    {
        if ((DateTime.Now - _lastRunTime) < RunInterval) return;
        _lastRunTime = DateTime.Now;
        WorldManager.EnqueueAction(new ActionEventDelegate(RefreshCoffeeDataTask));
    }

    private static void RefreshCoffeeDataTask()
    {
        log.Info("Running Terminal Coffee updates");
        var db = RedisManager.Redis.GetDatabase();
        try
        {
            db.KeyDelete(RedisIncomingKey);
            var coffees = WebClient.GetAsync<CoffeeResponse>("/product").Result;
            foreach (var coffee in coffees.data)
            {
                var productId = coffee.id;
                db.SetAdd(RedisIncomingKey, productId);
                db.HashSet(RedisDetailsKey, productId, JsonSerializer.Serialize(coffee));
            }

            var retiredProductIds = db.SetCombine(SetOperation.Difference, RedisVendorKey,
                RedisIncomingKey);
            foreach (var retiredProductId in retiredProductIds)
            {
                var weenieId = db.HashGet(RedisProductToWeeniesKey, retiredProductId);
                if (weenieId.HasValue && uint.TryParse(weenieId.ToString(), out var parsedId))
                {
                    RemoveFromVendor(BarkeepLienneVendorId, parsedId);
                    db.SetRemove(RedisVendorKey, retiredProductId);
                }
                else
                {
                    log.Warn($"No valid weenieId found for product ID {retiredProductId}");
                }
            }

            var productIdsToAdd = db.SetCombine(SetOperation.Difference, RedisIncomingKey,
                RedisCreatedKey);
            foreach (var productIdToAdd in productIdsToAdd)
            {
                var detailsJson = db.HashGet(RedisDetailsKey, productIdToAdd);
                if (!detailsJson.HasValue)
                {
                    log.Warn($"No valid details found for product ID {productIdToAdd}");
                    continue;
                }

                if (JsonSerializer.Deserialize<CoffeeProduct>(detailsJson.ToString()) is not { } details)
                {
                    log.Warn($"Failed to deserialize product details for {productIdToAdd}");
                    continue;
                }

                var weenieId = CreateWeenieFromCoffeeProduct(details);
                if (weenieId == null)
                {
                    log.Warn(
                        $"Skipping the creation of weenie for product ID {productIdToAdd} because it already exists");
                    var foundWeenieId = uint.Parse(db.HashGet(RedisProductToWeeniesKey, productIdToAdd).ToString());
                    AddToVendor(BarkeepLienneVendorId, foundWeenieId);
                }
                else
                {
                    db.HashSet(RedisProductToWeeniesKey, productIdToAdd, weenieId);
                    db.HashSet(RedisWeeniesToProductKey, weenieId, productIdToAdd);
                    AddToVendor(BarkeepLienneVendorId, (uint)weenieId);
                }

                db.SetAdd(RedisCreatedKey, productIdToAdd);
                db.SetAdd(RedisVendorKey, productIdToAdd);
            }

            db.KeyExpire(RedisIncomingKey, TimeSpan.FromMinutes(10));
        }
        catch (Exception ex)
        {
            log.Error("Failed to get terminal coffee updates", ex);
        }
    }

    private static uint GetCoffeeWeenieIdFromClassName(string weenieName)
    {
        using var ctx = new WorldDbContext();
        var foundWeenieId = ctx.Weenie.Where(w => w.ClassName == weenieName).Select(w => w.ClassId)
            .FirstOrDefault();
        return foundWeenieId;
    }

    private static void RemoveFromVendor(uint vendorId, uint coffeeWeenieId)
    {
        log.Info($"Removing coffee with weenie ID {coffeeWeenieId} from vendor {vendorId}.");

        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();
        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();

            if (!innerCtx.WeeniePropertiesCreateList.Any(w =>
                    w.ObjectId == vendorId && w.WeenieClassId == coffeeWeenieId))
            {
                log.Warn(
                    $"Weenie {coffeeWeenieId} not removed from vendor because vendor {vendorId} does not sell it.");
                return;
            }

            var coffeeVendorItemToRemove = innerCtx.WeeniePropertiesCreateList.FirstOrDefault(w =>
                w.ObjectId == vendorId && w.WeenieClassId == coffeeWeenieId);
            if (coffeeVendorItemToRemove != null)
            {
                innerCtx.WeeniePropertiesCreateList.Remove(coffeeVendorItemToRemove);
            }

            innerCtx.SaveChanges();
            transaction.Commit();
        });
    }

    private static uint? CreateWeenieFromCoffeeProduct(CoffeeProduct coffeeDetails)
    {
        var name = coffeeDetails.name;
        var id = coffeeDetails.id;
        var description = coffeeDetails.description;

        var variantName = "0oz";
        var price = 0;
        // Intentionally only looking at the first variant!
        var variants = coffeeDetails.variants;
        if (variants?.Count > 0)
        {
            var firstVariant = variants[0];
            variantName = firstVariant.name;
            price = firstVariant.price / 100;
        }

        var generatedWeenieId = CreateWeenieAndUpdateProperties(name, id, description, variantName, price);
        if (generatedWeenieId != null)
        {
            log.Info(
                $"Creating weenie for coffee with name {name}, id {id}, description {description}, type {variantName}, price {price}.");
        }
        else
        {
            log.Warn($"Did not create a weenie for coffee with name {name} because it already exists.");
        }

        return generatedWeenieId;
    }

    private static uint GetNextAvailableWeenieId(WorldDbContext ctx, uint minId = 100000)
    {
        var max = ctx.Weenie.Where(w => w.ClassId >= minId).Max(w => (uint?)w.ClassId) ?? minId;

        return max + 1;
    }

    private static uint? CreateWeenieAndUpdateProperties(string name, string id, string description, string variantName,
        int price)
    {
        var now = DateTime.UtcNow;
        var weenieName = CreateWeenieClassName(name, id);

        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();
        uint? weenieId = null;

        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();

            if (innerCtx.Weenie.Any(w => w.ClassName == weenieName))
            {
                log.Warn($"A weenie with the same name {weenieName} already exists.");
                return;
            }

            var isSubscription = description.Contains("subscription");

            uint nextWeenieId = GetNextAvailableWeenieId(innerCtx);
            innerCtx.Weenie.Add(new Weenie()
            {
                ClassId = nextWeenieId,
                ClassName = weenieName,
                Type = (int)WeenieType.Food,
                LastModified = now
            });

            innerCtx.WeeniePropertiesInt.AddRange(CoffeeWeenieToIntProperties(price, nextWeenieId, isSubscription));
            innerCtx.WeeniePropertiesString.AddRange(CoffeeWeenieToStringProperties(name, description, variantName,
                nextWeenieId, isSubscription));
            innerCtx.WeeniePropertiesDID.AddRange(CoffeeWeenieToDidProperties(nextWeenieId, isSubscription));
            innerCtx.SaveChanges();
            transaction.Commit();

            weenieId = nextWeenieId;
        });

        return weenieId;
    }

    private static List<WeeniePropertiesDID> CoffeeWeenieToDidProperties(uint nextWeenieId, bool isSubscription)
    {
        return
        [
            new WeeniePropertiesDID()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.Setup, Value = 0x020000E9 },
            new WeeniePropertiesDID()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.SoundTable, Value = 0x20000014 },
            new WeeniePropertiesDID()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.Icon,
                Value = (uint)(isSubscription
                    ? 0x060013E4 /* same as 'Copper Scarab' */
                    : 0x06001D83) /* same as 'Roasted Beans' */
            },
            new WeeniePropertiesDID()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.PhysicsEffectTable, Value = 0x3400002B },
            new WeeniePropertiesDID()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.UseSound, Value = (uint)Sound.Eat1 }
        ];
    }

    private static List<WeeniePropertiesString> CoffeeWeenieToStringProperties(string name, string description,
        string variantName, uint nextWeenieId, bool isSubscription)
    {
        return
        [
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.Name,
                Value = isSubscription
                    ? $"Token of {name} Coffee Subscription"
                    : $"Bag of {name} Coffee"
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.Use,
                Value = isSubscription
                    ? "This is a token of your subscription. You can't do anything with it; you'll have to wait."
                    : "Grind these beans to brew them! Or eat them as-is, I guess. Still good for you."
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.ShortDesc,
                Value = isSubscription
                    ? $"A Token of {name} Subscription"
                    : $"A bag of {name} coffee beans"
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.LongDesc,
                Value = isSubscription
                    ? $"{description}"
                    : $"A bag of {name} coffee beans.\n\n{description}\n\n{variantName}"
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.PluralName,
                Value = isSubscription
                    ? $"Tokens of {name} Coffee Subscription"
                    : $"Bags of {name} Coffee"
            }
        ];
    }

    private static List<WeeniePropertiesInt> CoffeeWeenieToIntProperties(int price, uint nextWeenieId,
        bool isSubscription)
    {
        var propertiesInt = new List<WeeniePropertiesInt>()
        {
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.ItemType, Value = (int)ItemType.Food },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.EncumbranceVal, Value = 50 },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.Mass, Value = 25 },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.ValidLocations, Value = 0 },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.MaxStackSize, Value = 100 },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.StackSize, Value = 1 },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.StackUnitEncumbrance, Value = 50 },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.StackUnitMass, Value = 25 },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.StackUnitValue, Value = price },
            new()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyInt.ItemUseable,
                Value = (int)(isSubscription ? Usable.No : Usable.Contained)
            },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.Value, Value = price },
            new()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyInt.PhysicsState,
                Value = (int)(PhysicsState.Ethereal | PhysicsState.IgnoreCollisions | PhysicsState.Gravity)
            }
        };

        if (isSubscription) return propertiesInt;

        propertiesInt.Add(new WeeniePropertiesInt()
        {
            ObjectId = nextWeenieId, Type = (ushort)PropertyInt.BoosterEnum,
            Value = (int)PropertyAttribute2nd.Stamina
        });
        propertiesInt.Add(new WeeniePropertiesInt()
            { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.BoostValue, Value = 120 });

        return propertiesInt;
    }

    private static string CreateWeenieClassName(string name, string id)
    {
        var cleanName = Regex.Replace(name, @"[^0-9a-zA-Z ]", "").Replace('_', ' ').ToLowerInvariant();
        return $"coffee_{cleanName}_{id}";
    }

    private static void AddToVendor(uint vendorId, uint coffeeWeenieId)
    {
        log.Info($"Adding coffee with weenie id {coffeeWeenieId} to vendor.");

        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();
        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();

            if (innerCtx.WeeniePropertiesCreateList.Any(w =>
                    w.ObjectId == vendorId && w.WeenieClassId == coffeeWeenieId))
            {
                log.Warn($"Weenie {coffeeWeenieId} not added to vendor {vendorId} because vendor already sells it.");
                return;
            }

            innerCtx.WeeniePropertiesCreateList.Add(new WeeniePropertiesCreateList()
            {
                ObjectId = vendorId,
                DestinationType = (sbyte)DestinationType.Shop,
                WeenieClassId = coffeeWeenieId,
                StackSize = -1,
                Palette = 0, // ??
                Shade = 0,
                TryToBond = false,
            });

            innerCtx.SaveChanges();
            transaction.Commit();
        });
    }
}
