using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using log4net;
using Microsoft.EntityFrameworkCore;
using Weenie = ACE.Database.Models.World.Weenie;

namespace ACE.Server.Managers;

public static class TerminalCoffeeManager
{
    private static readonly ILog Log = LogManager.GetLogger(nameof(TerminalCoffeeManager));

    private const string RedisAccountIdToTokenKey = "coffee.accounts:tokens";
    private const string RedisTokenHasCardAndAddressKey = "coffee.accounts:token.is_ready";
    private const string RedisOrderRequestKey = "coffee.orders:incoming";
    private const string RedisOrderSuccessKey = "coffee.orders:success";
    private const string RedisOrderFailureKey = "coffee.orders:failure";
    private const uint BarkeepLienneVendorId = 694;
    private const string BarkeepLienneName = "Barkeep Lienne";
    private const string ValidTerminalTokenPattern = @"^trm_(test|live)_[a-zA-Z0-9]+$";
    private const uint HeavyGrinderWeenieId = 7823;
    private const uint FullBrewKettleWeenieId = 29201;
    private const uint BrewedCoffeeWeenieId = 2454;

    private static DateTime _lastCoffeeUpdateTime = DateTime.MinValue;
    private static DateTime _lastOrderStatusCheckTime = DateTime.MinValue;


    private static readonly TimeSpan CoffeeUpdateInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan OrderStatusCheckInterval = TimeSpan.FromSeconds(10);

    private static readonly TerminalWebClient WebClient = new();

    public static uint TokenParchmentWeenieId { get; private set; }

    public static bool IsCoffeeWeenie(uint weenieId)
    {
        using var ctx = new WorldDbContext();
        return ctx.Weenie.First(w => w.ClassId == weenieId).ClassName.Contains("prd_");
    }

    public static void CreateOrderRequest(Player player, List<ItemProfile> coffeeItemsToOrder)
    {
        var db = RedisManager.Redis.GetDatabase();
        var token = db.HashGet(RedisAccountIdToTokenKey, player.Account.AccountId).ToString();
        var addressList = WebClient.GetAsync<AddressResponse>("/address", token).Result;
        var cardList = WebClient.GetAsync<CardResponse>("/card", token).Result;

        // Already know at this point that the lists have at least one element.
        var addressId = addressList.data[0].id;
        var cardId = cardList.data[0].id;

        var body = new CreateOrderRequest
        {
            cardID = cardId,
            addressID = addressId,
            variants = coffeeItemsToOrder.ToDictionary(
                item => WeenieIdToVariantId(item.WeenieClassId),
                item => item.Amount
            )
        };
        var job = CreateOrderJob.FromPayload(body, token, player.Biota.Id);

        var json = JsonSerializer.Serialize(job);
        db.ListLeftPush(RedisOrderRequestKey, json);
        Log.Info($"Sent order to queue: {json}");
    }

    public static bool CanPlayerPurchaseCoffee(Player player)
    {
        var token = RedisManager.Redis.GetDatabase().HashGet(RedisAccountIdToTokenKey, player.Account.AccountId);
        var hasToken = token.HasValue;
        if (!hasToken) return false;

        return DoesCustomerHaveAddressAndCard(token.ToString());
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
        catch (Exception)
        {
            return false;
        }
    }

    private static bool DoesCustomerHaveAddressAndCard(string token)
    {
        var db = RedisManager.Redis.GetDatabase();
        var tokenIsReady = db.StringGet($"{RedisTokenHasCardAndAddressKey}:{token}");
        if (tokenIsReady.HasValue)
        {
            return tokenIsReady.Equals("1");
        }

        var addressList = WebClient.GetAsync<AddressResponse>("/address", token).Result;
        var cardList = WebClient.GetAsync<CardResponse>("/card", token).Result;

        var hasAddress = addressList.data.Count > 0;
        if (!hasAddress) Log.Info("Player does not have an address loaded.");

        var hasCard = cardList.data.Count > 0;
        if (!hasCard) Log.Info("Player does not have a card loaded.");

        // Assuming the presence of an address and a card means they are valid.
        var ready = hasAddress && hasCard;
        db.StringSet($"{RedisTokenHasCardAndAddressKey}:{token}", ready ? 1 : 0, TimeSpan.FromHours(1));
        return ready;
    }

    public static void HandleTokenParchment(Book book, Player player, Vendor vendor)
    {
        Log.Info(
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
        Log.Info($"Linked account to token.");
        player.Session.Network.EnqueueSend(new GameEventTell(vendor,
            "Your parchment has been received and your accounts have been linked.", player,
            ChatMessageType.Tell));
    }

    public static void Tick()
    {
        var now = DateTime.Now;

        RunIfDue(ref _lastCoffeeUpdateTime, CoffeeUpdateInterval, RefreshCoffeeDataTask, now);
        RunIfDue(ref _lastOrderStatusCheckTime, OrderStatusCheckInterval, OrderStatusTask, now);
    }

    private static void OrderStatusTask()
    {
        var db = RedisManager.Redis.GetDatabase();
        var success = db.ListRightPop(RedisOrderSuccessKey);
        var failure = db.ListRightPop(RedisOrderFailureKey);

        if (success.HasValue)
        {
            Log.Info($"Successful order: {success.ToString()}");
            var successJob = JsonSerializer.Deserialize<OrderSuccess>(success.ToString());
            var player = PlayerManager.GetOnlinePlayer(successJob.for_player_id);
            player.Session.Network.EnqueueSend(new GameEventTell(player.Session,
                $"Your order {successJob.order_id} was successful!",
                BarkeepLienneName,
                BarkeepLienneVendorId, player.Biota.Id, ChatMessageType.Tell));
        }

        if (failure.HasValue)
        {
            Log.Info($"Failed order: {failure.ToString()}");
            var failureJob = JsonSerializer.Deserialize<OrderFailure>(failure.ToString());
            var player = PlayerManager.GetOnlinePlayer(failureJob.for_player_id);
            player.Session.Network.EnqueueSend(new GameEventTell(player.Session,
                $"Your order processing failed! Please check with Terminal Coffee for next steps.",
                BarkeepLienneName,
                BarkeepLienneVendorId, player.Biota.Id, ChatMessageType.Tell));
        }
    }

    private static void RefreshCoffeeDataTask()
    {
        Log.Info("Running Terminal Coffee updates");
        try
        {
            var tokenParchmentWeenie = CreateTokenParchmentWeenie();
            TokenParchmentWeenieId = tokenParchmentWeenie.ClassId;
            AddTokenParchmentToVendor(BarkeepLienneVendorId, tokenParchmentWeenie);

            var coffees = WebClient.GetAsync<CoffeeResponse>("/product").Result;
            var wholeAndGround = new Dictionary<string, List<Weenie>>();

            foreach (var weenie in coffees.data.SelectMany(CreateWeeniesFromCoffeeProduct))
            {
                if (!weenie.ClassName.Contains("cron"))
                {
                    wholeAndGround.Add(weenie.ClassName, [weenie]);
                }

                AddCoffeeWeenieToVendor(BarkeepLienneVendorId, weenie);
            }

            foreach (var groundWeenie in coffees.data.Where(d => !d.description.Contains("subscription"))
                         .SelectMany(CreateGroundWeeniesFromCoffeeProduct))
            {
                wholeAndGround[groundWeenie.ClassName.Replace("_ground", "")].Add(groundWeenie);
            }

            CreateRecipesForCoffees(wholeAndGround);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to get terminal coffee updates", ex);
        }
    }


    private static void CreateRecipesForCoffees(Dictionary<string, List<Weenie>> wholeAndGround)
    {
        // Heavy Grinder on Bag (not cron) -> 17 Ground Coffee Piles
        // existing: Brew Kettle on water -> Full Brew Kettle
        // Full Brew Kettle on Ground Coffee Pile -> Coffee

        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();
        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();
            // Using this for recipe ids, too.
            var nextWeenieId = GetNextAvailableWeenieId(innerCtx);

            var targetTypes = innerCtx.WeeniePropertiesInt.FirstOrDefault(w =>
                w.ObjectId == FullBrewKettleWeenieId && w.Type == (ushort)PropertyInt.TargetType);
            if (targetTypes == null)
            {
                innerCtx.WeeniePropertiesInt.Add(new WeeniePropertiesInt
                {
                    ObjectId = FullBrewKettleWeenieId,
                    Type = (ushort)PropertyInt.TargetType,
                    Value = (int)ItemType.Food
                });
            }
            else
            {
                targetTypes.Value |= (int)ItemType.Food;
            }

            innerCtx.SaveChanges();

            foreach (var kv in wholeAndGround)
            {
                var whole = kv.Value[0];
                var ground = kv.Value[1];

                var name = innerCtx.WeeniePropertiesString
                    .Where(wps => wps.ObjectId == whole.ClassId && wps.Type == (int)PropertyString.Name)
                    .Select(wps => wps.Value).First();
                if (name == null)
                {
                    Log.Warn($"Found coffee without name on weenie {whole.ClassName}.");
                    return;
                }

                var match = Regex.Match(name, @"^Bag of (?<name>.*?) Coffee.*?(?<size>\d+)oz");
                var coffeeName = match.Groups["name"].Value;
                var size = int.Parse(match.Groups["size"].Value);

                Log.Info($"Creating recipe for grinding {whole.ClassId} into {ground.ClassId}");

                innerCtx.Recipe.Add(new Recipe
                {
                    Id = nextWeenieId,
                    Unknown1 = 0,
                    Skill = (uint)Skill.Cooking,
                    Difficulty = 1,
                    SalvageType = 0,
                    SuccessWCID = ground.ClassId,
                    SuccessAmount =
                        (uint)Math.Floor(size * 28.35 / 20.0), /* 20 gram dose; size e.g. 12 oz -> ~340 gram */
                    SuccessMessage = $"You grind the {coffeeName} beans.",
                    FailWCID = 0,
                    FailAmount = 0,
                    FailMessage = $"You fail to grind the {coffeeName} beans.",
                    SuccessDestroySourceChance = 0,
                    SuccessDestroySourceAmount = 0,
                    SuccessDestroySourceMessage = null,
                    SuccessDestroyTargetChance = 1,
                    SuccessDestroyTargetAmount = 1,
                    SuccessDestroyTargetMessage = null,
                    FailDestroySourceChance = 0,
                    FailDestroySourceAmount = 0,
                    FailDestroySourceMessage = null,
                    FailDestroyTargetChance = 1,
                    FailDestroyTargetAmount = 1,
                    FailDestroyTargetMessage = null,
                    DataId = 0,
                    LastModified = DateTime.Now
                });

                Log.Info($"Creating recipe for brewing ground {ground.ClassId} into brewed coffee");

                innerCtx.Recipe.Add(new Recipe
                {
                    Id = nextWeenieId + 1,
                    Unknown1 = 0,
                    Skill = (uint)Skill.Cooking,
                    Difficulty = 1,
                    SalvageType = 0,
                    SuccessWCID = BrewedCoffeeWeenieId,
                    SuccessAmount = 1,
                    SuccessMessage = $"You brew the {coffeeName} dose.",
                    FailWCID = 0,
                    FailAmount = 0,
                    FailMessage = $"You fail to brew the {coffeeName} dose.",
                    SuccessDestroySourceChance = 0,
                    SuccessDestroySourceAmount = 0,
                    SuccessDestroySourceMessage = null,
                    SuccessDestroyTargetChance = 1,
                    SuccessDestroyTargetAmount = 1,
                    SuccessDestroyTargetMessage = null,
                    FailDestroySourceChance = 0,
                    FailDestroySourceAmount = 0,
                    FailDestroySourceMessage = null,
                    FailDestroyTargetChance = 1,
                    FailDestroyTargetAmount = 1,
                    FailDestroyTargetMessage = null,
                    DataId = 0,
                    LastModified = DateTime.Now
                });

                innerCtx.CookBook.Add(new CookBook
                {
                    RecipeId = nextWeenieId,
                    SourceWCID = HeavyGrinderWeenieId,
                    TargetWCID = whole.ClassId,
                    LastModified = DateTime.Now
                });

                innerCtx.CookBook.Add(new CookBook
                {
                    RecipeId = nextWeenieId + 1,
                    SourceWCID = FullBrewKettleWeenieId,
                    TargetWCID = ground.ClassId,
                    LastModified = DateTime.Now
                });
                nextWeenieId += 2;
            }

            innerCtx.SaveChanges();
            transaction.Commit();
        });
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
                Log.Info($"A weenie with the same name {weenieName} already exists.");
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

    private static void AddTokenParchmentToVendor(uint vendorId, Weenie tokenParchmentWeenie)
    {
        var tokenParchmentWeenieId = tokenParchmentWeenie.ClassId;

        Log.Info($"Adding token parchment with weenie id {tokenParchmentWeenieId} to vendor.");
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
                Log.Info($"Altering {vendorId} to accept Writable item types from players.");
                merchandiseTypes.Value |= (int)ItemType.Writable;
                innerCtx.SaveChanges();
            }

            if (innerCtx.WeeniePropertiesCreateList.Any(w =>
                    w.ObjectId == vendorId && w.WeenieClassId == tokenParchmentWeenieId))
            {
                Log.Warn(
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

    private static uint GetNextAvailableWeenieId(WorldDbContext ctx, uint minId = 100000)
    {
        var max = ctx.Weenie.Where(w => w.ClassId >= minId).Max(w => (uint?)w.ClassId) ?? minId;

        return max + 1;
    }

    private static string CreateWeenieClassName(string name, string id, string variantId, string suffix = "")
    {
        var cleanName = Regex.Replace(name, @"[^0-9a-zA-Z ]", "").Replace('_', ' ').ToLowerInvariant();
        return $"coffee_{cleanName}_{id}_{variantId}{suffix}";
    }

    private static string WeenieIdToVariantId(uint weenieId)
    {
        using var ctx = new WorldDbContext();
        var weenieName = ctx.Weenie.FirstOrDefault(w => w.ClassId == weenieId)?.ClassName;

        // The weenie we're looking up should always exist.
        if (weenieName != null) return weenieName[weenieName.IndexOf("var_", StringComparison.Ordinal)..];

        Log.Error($"Attempted to look up weenie id {weenieId} and couldn't find it.");
        return null;
    }

    // Duplication...
    private static List<Weenie> CreateGroundWeeniesFromCoffeeProduct(CoffeeProduct coffeeDetails)
    {
        if (coffeeDetails.variants.Count < 1)
        {
            Log.Warn(
                $"Coffee product with name {coffeeDetails.name} and id {coffeeDetails.id} has no variants. Creating a bare weenie.");
            var generatedWeenie = CreateGroundWeenieAndUpdateProperties(coffeeDetails.name, coffeeDetails.id,
                coffeeDetails.description, "var_xxx", "Unknown Variant", 0);
            return [generatedWeenie];
        }

        var generatedWeenies = new List<Weenie>();
        foreach (var variant in coffeeDetails.variants)
        {
            Log.Info(
                $"Creating weenie for ground coffee with name {coffeeDetails.name}, id {coffeeDetails.id}, description {coffeeDetails.description}, type {variant.name}, price {variant.price / 100}.");
            generatedWeenies.Add(CreateGroundWeenieAndUpdateProperties(coffeeDetails.name, coffeeDetails.id,
                coffeeDetails.description, variant.id, variant.name, variant.price / 100));
        }

        return generatedWeenies;
    }

    private static List<Weenie> CreateWeeniesFromCoffeeProduct(CoffeeProduct coffeeDetails)
    {
        if (coffeeDetails.variants.Count < 1)
        {
            Log.Warn(
                $"Coffee product with name {coffeeDetails.name} and id {coffeeDetails.id} has no variants. Creating a bare weenie.");
            var generatedWeenie = CreateWeenieAndUpdateProperties(coffeeDetails.name, coffeeDetails.id,
                coffeeDetails.description, "var_xxx", "Unknown Variant", 0);
            return [generatedWeenie];
        }

        var generatedWeenies = new List<Weenie>();
        foreach (var variant in coffeeDetails.variants)
        {
            Log.Info(
                $"Creating weenie for coffee with name {coffeeDetails.name}, id {coffeeDetails.id}, description {coffeeDetails.description}, type {variant.name}, price {variant.price / 100}.");
            generatedWeenies.Add(CreateWeenieAndUpdateProperties(coffeeDetails.name, coffeeDetails.id,
                coffeeDetails.description, variant.id, variant.name, variant.price / 100));
        }

        return generatedWeenies;
    }

    private static Weenie CreateGroundWeenieAndUpdateProperties(string name, string id, string description,
        string variantId,
        string variantName, int price)
    {
        var now = DateTime.UtcNow;
        var weenieName = CreateWeenieClassName(name, id, variantId, "_ground");

        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();

        Weenie weenie = null;
        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();
            weenie = innerCtx.Weenie.FirstOrDefault(w => w.ClassName == weenieName);
            if (weenie != null)
            {
                Log.Warn($"A weenie with the same name {weenieName} already exists.");
                return;
            }

            var nextWeenieId = GetNextAvailableWeenieId(innerCtx);
            weenie = new Weenie()
            {
                ClassId = nextWeenieId,
                ClassName = weenieName,
                Type = (int)WeenieType.Food,
                LastModified = now
            };
            innerCtx.Weenie.Add(weenie);

            innerCtx.WeeniePropertiesInt.AddRange(GroundCoffeeWeenieIntProperties(price, nextWeenieId));
            innerCtx.WeeniePropertiesString.AddRange(GroundCoffeeWeenieStringProperties(name, description, variantName,
                nextWeenieId));
            innerCtx.WeeniePropertiesDID.AddRange(GroundCoffeeWeenieDidProperties(nextWeenieId));
            innerCtx.SaveChanges();
            transaction.Commit();
        });
        return weenie;
    }

    private static Weenie CreateWeenieAndUpdateProperties(string name, string id, string description,
        string variantId,
        string variantName,
        int price)
    {
        var now = DateTime.UtcNow;
        var weenieName = CreateWeenieClassName(name, id, variantId);

        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();

        Weenie weenie = null;
        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();
            weenie = innerCtx.Weenie.FirstOrDefault(w => w.ClassName == weenieName);
            if (weenie != null)
            {
                Log.Warn($"A weenie with the same name {weenieName} already exists.");
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

            innerCtx.WeeniePropertiesInt.AddRange(CoffeeWeenieIntProperties(price, nextWeenieId, isSubscription));
            innerCtx.WeeniePropertiesString.AddRange(CoffeeWeenieStringProperties(name, description, variantName,
                nextWeenieId, isSubscription));
            innerCtx.WeeniePropertiesDID.AddRange(CoffeeWeenieDidProperties(nextWeenieId, isSubscription));
            innerCtx.SaveChanges();
            transaction.Commit();
        });
        return weenie;
    }

    private static List<WeeniePropertiesDID> GroundCoffeeWeenieDidProperties(uint nextWeenieId)
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
                Value = 0x06001D86 /* same as 'Cocoa Mixture' */
            },
            new WeeniePropertiesDID()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.PhysicsEffectTable, Value = 0x3400002B },
            new WeeniePropertiesDID()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyDataId.UseSound, Value = (uint)Sound.Eat1 }
        ];
    }

    private static List<WeeniePropertiesDID> CoffeeWeenieDidProperties(uint nextWeenieId,
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

    private static List<WeeniePropertiesString> GroundCoffeeWeenieStringProperties(string name,
        string description,
        string variantName, uint nextWeenieId)
    {
        return
        [
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.Name,
                Value = $"20g Dose of Ground {name} Coffee ({variantName})"
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.Use,
                Value = "These beans are ready to brew. Again, I guess you could consume them directly."
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.ShortDesc,
                Value = $"20g Dose of ground {name} coffee beans ({variantName})"
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.LongDesc,
                Value = $"20g Dose of ground {name} coffee beans.\n\n{description}"
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.PluralName,
                Value = $"20g Doses of ground {name} Coffee ({variantName})"
            }
        ];
    }

    private static List<WeeniePropertiesString> CoffeeWeenieStringProperties(string name,
        string description,
        string variantName, uint nextWeenieId, bool isSubscription)
    {
        return
        [
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.Name,
                Value = isSubscription
                    ? $"Token of {name} Coffee Subscription ({variantName})"
                    : $"Bag of {name} Coffee ({variantName})"
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
                    ? $"A Token of {name} Subscription ({variantName})"
                    : $"A bag of {name} coffee beans ({variantName})"
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.LongDesc,
                Value = isSubscription
                    ? $"{description}\n\n{variantName}"
                    : $"A bag of {name} coffee beans.\n\n{description}\n\n{variantName}"
            },
            new WeeniePropertiesString()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyString.PluralName,
                Value = isSubscription
                    ? $"Tokens of {name} Coffee Subscription ({variantName})"
                    : $"Bags of {name} Coffee ({variantName})"
            }
        ];
    }

    private static List<WeeniePropertiesInt> GroundCoffeeWeenieIntProperties(int price, uint nextWeenieId)
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
                Value = (int)Usable.Contained
            },
            new()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.Value, Value = price },
            new()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyInt.PhysicsState,
                Value = (int)(PhysicsState.Ethereal | PhysicsState.IgnoreCollisions | PhysicsState.Gravity)
            },
            new WeeniePropertiesInt()
            {
                ObjectId = nextWeenieId, Type = (ushort)PropertyInt.BoosterEnum,
                Value = (int)PropertyAttribute2nd.Stamina
            },
            new WeeniePropertiesInt()
                { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.BoostValue, Value = 12 }
        };
        return propertiesInt;
    }

    private static List<WeeniePropertiesInt> CoffeeWeenieIntProperties(int price, uint nextWeenieId,
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
            { ObjectId = nextWeenieId, Type = (ushort)PropertyInt.BoostValue, Value = 12 });
        return propertiesInt;
    }

    private static void AddCoffeeWeenieToVendor(uint vendorId, Weenie coffeeWeenie)
    {
        var coffeeWeenieId = coffeeWeenie.ClassId;

        Log.Info($"Adding coffee with weenie id {coffeeWeenieId} to vendor.");
        using var ctx = new WorldDbContext();
        var strategy = ctx.Database.CreateExecutionStrategy();
        strategy.Execute(() =>
        {
            using var innerCtx = new WorldDbContext();
            using var transaction = innerCtx.Database.BeginTransaction();
            if (innerCtx.WeeniePropertiesCreateList.Any(w =>
                    w.ObjectId == vendorId && w.WeenieClassId == coffeeWeenieId))
            {
                Log.Warn($"Weenie {coffeeWeenieId} not added to vendor {vendorId} because vendor already sells it.");
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

    private static void RunIfDue(ref DateTime lastRunTime, TimeSpan interval, Action task, DateTime now)
    {
        if ((now - lastRunTime) < interval) return;

        lastRunTime = now;
        WorldManager.EnqueueAction(new ActionEventDelegate(task));
    }
}
