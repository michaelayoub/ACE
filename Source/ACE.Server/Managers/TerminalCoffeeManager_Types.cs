using System;
using System.Collections.Generic;

namespace ACE.Server.Managers;

public class OrderSuccess
{
    public string order_id { get; set; }
    public uint for_player_id { get; set; }
    public string type { get; set; } = "success";
}

public class OrderFailure
{
    public uint for_player_id { get; set; }
    public string type { get; set; } = "failure";
}

public class Variant
{
    public string id { get; set; }
    public string name { get; set; }
    public int price { get; set; }
}

public class CoffeeProduct
{
    public string id { get; set; }
    public string name { get; set; }
    public string description { get; set; }
    public List<Variant> variants { get; set; }
}

public class CoffeeResponse
{
    public List<CoffeeProduct> data { get; set; }
}

public class Card
{
    public string id { get; set; }
}

public class CardResponse
{
    public List<Card> data { get; set; }
}

public class Address
{
    public string id { get; set; }
}

public class AddressResponse
{
    public List<Address> data { get; set; }
}

public class CreateOrderRequest
{
    public string cardID { get; set; }
    public string addressID { get; set; }
    public Dictionary<string, int> variants { get; set; }
}

public class CreateOrderJob
{
    public CreateOrderRequest payload { get; set; }
    public Guid id { get; set; }
    public string type { get; set; }
    public string token { get; set; }
    public uint for_player_id { get; set; }
    public int retries { get; set; }
    public DateTime next_attempt { get; set; }

    public static CreateOrderJob FromPayload(CreateOrderRequest payload, string token, uint forPlayerId)
    {
        return new CreateOrderJob
        {
            payload = payload,
            id = Guid.NewGuid(),
            type = "create_order",
            token = token,
            for_player_id = forPlayerId,
            retries = 0,
            next_attempt = DateTime.MinValue
        };
    }
}
