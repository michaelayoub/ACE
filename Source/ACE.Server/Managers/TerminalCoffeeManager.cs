using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using log4net;
using Microsoft.EntityFrameworkCore;
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

    private const string RedisAccountIdToTokenKey = "coffee.accounts:tokens";
    private const uint BarkeepLienneVendorId = 694;

    private static DateTime _lastRunTime = DateTime.MinValue;
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(12);
    private static readonly TerminalWebClient WebClient = new();

    public static uint TokenParchmentWeenieId { get; private set; }

    private static readonly string ValidTerminalTokenPattern = @"^trm_(test|live)_[a-zA-Z0-9]+$";

    public static void Tick()
    {
        if ((DateTime.Now - _lastRunTime) < RunInterval) return;
        _lastRunTime = DateTime.Now;
        WorldManager.EnqueueAction(new ActionEventDelegate(RefreshCoffeeDataTask));
    }

    public static bool IsCoffeeWeenie(uint weenieId)
    {
        using var ctx = new WorldDbContext();
        return ctx.Weenie.First(w => w.ClassId == weenieId).ClassName.Contains("prd_");
    }

    public static bool CanPlayerPurchaseCoffee(Player player)
    {
        return RedisManager.Redis.GetDatabase().HashExists(RedisAccountIdToTokenKey, player.Account.AccountId);
    }

    private static void RefreshCoffeeDataTask()
    {
        log.Info("Running Terminal Coffee updates");
        try
        {
            var tokenParchmentWeenie = CreateTokenParchmentWeenie();
            TokenParchmentWeenieId = tokenParchmentWeenie.ClassId;
            AddTokenParchmentToVendor(BarkeepLienneVendorId, tokenParchmentWeenie);

            var coffees = WebClient.GetAsync<CoffeeResponse>("/product").Result;

            // TODO: Remove retired coffees from the vendor. I think I'll need to remove the rows from
            // the world table, find the biota corresponding to that vendor, load it, and, uh, call ??? method.

            foreach (var weenie in coffees.data.Select(CreateWeenieFromCoffeeProduct))
            {
                AddToVendor(BarkeepLienneVendorId, weenie);
            }
        }
        catch (Exception ex)
        {
            log.Error("Failed to get terminal coffee updates", ex);
        }
    }

    private static void AddTokenParchmentToVendor(uint vendorId, Weenie tokenParchmentWeenie)
    {
        var tokenParchmentWeenieId = tokenParchmentWeenie.ClassId;

        log.Info($"Adding token parchment with weenie id {tokenParchmentWeenieId} to vendor.");
        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();
        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();

            // The vendor needs to accept it as well.
            var merchandiseTypes = innerCtx.WeeniePropertiesInt.FirstOrDefault(w =>
                w.ObjectId == vendorId && w.Type == (ushort)PropertyInt.MerchandiseItemTypes);
            if (merchandiseTypes != null && (merchandiseTypes.Value & (int)ItemType.Writable) == 0)
            {
                log.Info($"Altering {vendorId} to accept Writable item types from players.");
                merchandiseTypes.Value |= (int)ItemType.Writable;
                innerCtx.SaveChanges();
            }

            if (innerCtx.WeeniePropertiesCreateList.Any(w =>
                    w.ObjectId == vendorId && w.WeenieClassId == tokenParchmentWeenieId))
            {
                log.Warn(
                    $"Weenie {tokenParchmentWeenieId} not added to vendor {vendorId} because vendor already sells it.");
                transaction.Commit();
                return;
            }

            innerCtx.WeeniePropertiesCreateList.Add(new WeeniePropertiesCreateList()
            {
                ObjectId = vendorId,
                DestinationType = (sbyte)DestinationType.Shop,
                WeenieClassId = tokenParchmentWeenieId,
                StackSize = -1,
                Palette = 0, // ??
                Shade = 0,
                TryToBond = false,
            });
            innerCtx.SaveChanges();
            transaction.Commit();
        });
    }

    private static Weenie CreateWeenieFromCoffeeProduct(CoffeeProduct coffeeDetails)
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
            log.Error($"Something went wrong creating a weenie for coffee {coffeeDetails.name} ({coffeeDetails.id}).");
        }

        return generatedWeenieId;
    }

    private static uint GetNextAvailableWeenieId(WorldDbContext ctx, uint minId = 100000)
    {
        var max = ctx.Weenie.Where(w => w.ClassId >= minId).Max(w => (uint?)w.ClassId) ?? minId;

        return max + 1;
    }

    private static Weenie CreateTokenParchmentWeenie()
    {
        var now = DateTime.UtcNow;
        var weenieName = "terminal_coffee_token_parchment";

        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();
        Weenie weenie = null;

        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();
            var existing = innerCtx.Weenie.FirstOrDefault(w => w.ClassName == weenieName);
            if (existing != null)
            {
                log.Info($"A weenie with the same name {weenieName} already exists.");
                weenie = existing;
                return;
            }

            var nextWeenieId = GetNextAvailableWeenieId(innerCtx);
            weenie = new Weenie
            {
                ClassId = nextWeenieId,
                ClassName = weenieName,
                Type = (int)WeenieType.Book,
                LastModified = now
            };
            innerCtx.Weenie.Add(weenie);
            innerCtx.WeeniePropertiesInt.AddRange(
                new WeeniePropertiesInt
                    { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.ItemType, Value = (int)ItemType.Writable },
                new WeeniePropertiesInt
                    { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.EncumbranceVal, Value = 25 },
                new WeeniePropertiesInt { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.Mass, Value = 5 },
                new WeeniePropertiesInt
                    { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.ValidLocations, Value = 0 },
                new WeeniePropertiesInt
                    { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.ItemUseable, Value = (int)Usable.Contained },
                new WeeniePropertiesInt { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.Value, Value = 1 },
                new WeeniePropertiesInt { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.Bonded, Value = 1 },
                new WeeniePropertiesInt
                {
                    ObjectId = nextWeenieId, Type = (ushort)PropertyInt.PhysicsState, Value =
                        (int)(PhysicsState.Ethereal | PhysicsState.IgnoreCollisions | PhysicsState.Gravity)
                });
            innerCtx.WeeniePropertiesBool.Add(new WeeniePropertiesBool
                { ObjectId = nextWeenieId, Type = (ushort)PropertyBool.Inscribable, Value = true });
            innerCtx.WeeniePropertiesFloat.Add(new WeeniePropertiesFloat
                { ObjectId = nextWeenieId, Type = (ushort)PropertyFloat.UseRadius, Value = 1 });
            innerCtx.WeeniePropertiesString.AddRange(new WeeniePropertiesString
                    { ObjectId = nextWeenieId, Type = (ushort)PropertyString.Name, Value = "Registration Parchment" },
                new WeeniePropertiesString
                {
                    ObjectId = nextWeenieId, Type = (ushort)PropertyString.ShortDesc,
                    Value = "This parchment enables you to provide your Terminal Coffee personal access token."
                },
                new WeeniePropertiesString
                {
                    ObjectId = nextWeenieId, Type = (ushort)PropertyString.LongDesc, Value =
                        "This parchment enables you to provide your Terminal Coffee personal access token. To create " +
                        "one, see the instructions at https://www.terminal.shop/api/#authentication. Ensure you set up " +
                        "a delivery address and credit card, as well."
                });
            innerCtx.WeeniePropertiesDID.AddRange(
                new WeeniePropertiesDID
                    { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.Setup, Value = 0x02000155 },
                new WeeniePropertiesDID
                    { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.SoundTable, Value = 0x20000014 },
                new WeeniePropertiesDID
                    { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.Icon, Value = 0x06001310 },
                new WeeniePropertiesDID
                    { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.PhysicsEffectTable, Value = 0x3400002B });
            innerCtx.WeeniePropertiesBook.Add(new WeeniePropertiesBook
                { ObjectId = nextWeenieId, MaxNumPages = 2, MaxNumCharsPerPage = 1000 });
            innerCtx.WeeniePropertiesBookPageData.Add(new WeeniePropertiesBookPageData
            {
                ObjectId = nextWeenieId,
                PageId = 0,
                AuthorId = 0xFFFFFFFF,
                AuthorName = "terminal",
                AuthorAccount = "Prewritten",
                IgnoreAuthor = true,
                PageText =
                    "To establish your identity as a Terminal Coffee customer, you need to provide a personal access token.\n\n" +
                    "Once you have it, write it on the next page. There should be no other text on that page other than your token. " +
                    "After you've added it to the next page, return it to this vendor and sell it back."
            });
            innerCtx.SaveChanges();
            transaction.Commit();
        });
        return weenie;
    }

    private static Weenie CreateWeenieAndUpdateProperties(string name, string id, string description,
        string variantName,
        int price)
    {
        var now = DateTime.UtcNow;
        var weenieName = CreateWeenieClassName(name, id);
        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();
        Weenie weenie = null;
        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();
            var existing = innerCtx.Weenie.FirstOrDefault(w => w.ClassName == weenieName);
            if (existing != null)
            {
                log.Warn($"A weenie with the same name {weenieName} already exists.");
                weenie = existing;
                return;
            }

            var isSubscription = description.Contains("subscription");
            var nextWeenieId = GetNextAvailableWeenieId(innerCtx);
            weenie = new Weenie()
            {
                ClassId = nextWeenieId,
                ClassName = weenieName,
                Type = (int)WeenieType.Food,
                LastModified = now
            };
            innerCtx.Weenie.Add(weenie);

            innerCtx.WeeniePropertiesInt.AddRange(CoffeeWeenieToIntProperties(price, nextWeenieId, isSubscription));
            innerCtx.WeeniePropertiesString.AddRange(CoffeeWeenieToStringProperties(name, description, variantName,
                nextWeenieId, isSubscription));
            innerCtx.WeeniePropertiesDID.AddRange(CoffeeWeenieToDidProperties(nextWeenieId, isSubscription));
            innerCtx.SaveChanges();
            transaction.Commit();
        });
        return weenie;
    }

    private static List<WeeniePropertiesDID> CoffeeWeenieToDidProperties(uint nextWeenieId,
        bool isSubscription)
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

    private static List<WeeniePropertiesString> CoffeeWeenieToStringProperties(string name,
        string description,
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

    private static void AddToVendor(uint vendorId, Weenie coffeeWeenie)
    {
        var coffeeWeenieId = coffeeWeenie.ClassId;

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

    public static bool IsTokenParchmentValid(Book book)
    {
        if (book == null) return false;
        if (book.AppraisalPages < 2) return false;
        if (book.GetPage(1) == null) return false;
        if (book.GetPage(1).PageText == null) return false;

        var tokenPageContent = book.GetPage(1).PageText.Trim();
        var matchesForm = Regex.IsMatch(tokenPageContent, ValidTerminalTokenPattern);
        try
        {
            var result = WebClient.GetAsync<object>("/profile", tokenPageContent).Result;
            return result != null && matchesForm;
        }
        catch (Exception e)
        {
            return false;
        }
    }

    public static void HandleTokenParchment(Book book, Player player, Vendor vendor)
    {
        log.Info(
            $"Handling token parchment for player {player.Name} on account {player.Account.AccountName} ({player.Account.AccountId}).");

        // Parchment has already been verified, but we'll still check. This really shouldn't happen.
        if (book.AppraisalPages < 2 || book.GetPage(1) == null || !IsTokenParchmentValid(book))
        {
            player.Session.Network.EnqueueSend(new GameEventTell(vendor,
                "I received your parchment, but it is invalid. Please try again.", player,
                ChatMessageType.Tell));
        }

        var token = book.GetPage(1).PageText.Trim();
        RedisManager.Redis.GetDatabase().HashSet(RedisAccountIdToTokenKey, player.Account.AccountId, token);
        log.Info($"Linked account to token.");
        player.Session.Network.EnqueueSend(new GameEventTell(vendor,
            "Your parchment has been received and your accounts have been linked.", player,
            ChatMessageType.Tell));
    }
}
