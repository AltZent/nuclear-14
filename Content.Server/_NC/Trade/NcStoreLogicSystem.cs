using System.Linq;
using Content.Shared._NC.Trade;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;


namespace Content.Server._NC.Trade;


public sealed class NcStoreLogicSystem : EntitySystem
{
    private static readonly ISawmill Sawmill = Logger.GetSawmill("ncstore-logic");
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly IEntityManager _ents = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly IPrototypeManager _protos = default!;
    [Dependency] private readonly SharedStackSystem _stacks = default!;

    public int GetBalance(EntityUid user, string stackType)
    {
        var total = 0;
        foreach (var entity in EnumerateDeepItemsUnique(user))
            if (_ents.TryGetComponent(entity, out StackComponent? stack)
                && stack.StackTypeId == stackType)
                total += stack.Count;
        return total;
    }

    private bool TryPickCurrencyForBuy(
        NcStoreComponent store,
        StoreListingPrototype listing,
        EntityUid user,
        out string currency,
        out int price
    )
    {
        currency = string.Empty;
        price = 0;

        var balances = new Dictionary<string, int>(store.CurrencyWhitelist.Count);
        foreach (var cur in store.CurrencyWhitelist)
            balances[cur] = GetBalance(user, cur);

        foreach (var cur in store.CurrencyWhitelist)
        {
            if (!listing.Cost.TryGetValue(cur, out var priceF))
                continue;

            var p = (int) MathF.Ceiling(priceF);
            if (p <= 0)
                continue;

            if (!balances.TryGetValue(cur, out var bal) || bal < p)
                continue;

            currency = cur;
            price = p;
            return true;
        }

        return false;
    }


    private bool TryPickCurrencyForSell(
        NcStoreComponent store,
        StoreListingPrototype listing,
        out string currency,
        out int price
    )
    {
        currency = string.Empty;
        price = 0;

        foreach (var cur in store.CurrencyWhitelist)
        {
            if (!listing.Cost.TryGetValue(cur, out var priceF))
                continue;

            var p = (int) MathF.Ceiling(priceF);
            if (p <= 0)
                continue;

            currency = cur;
            price = p;
            return true;
        }

        return false;
    }

    public bool TryBuy(string listingId, EntityUid machine, NcStoreComponent? store, EntityUid user, int count = 1)
    {
        if (store == null || store.Listings.Count == 0 || count <= 0)
            return false;

        var listing = store.Listings.FirstOrDefault(x => x.Id == listingId && x.Mode == StoreMode.Buy);
        if (listing == null)
            return false;

        if (!_protos.TryIndex<EntityPrototype>(listing.ProductEntity, out _))
            return false;

        if (!TryPickCurrencyForBuy(store, listing, user, out var currency, out var unitPrice))
            return false;

        var maxByRemaining = listing.RemainingCount >= 0 ? listing.RemainingCount : int.MaxValue;

        var balance = GetBalance(user, currency);
        var maxByMoney = unitPrice > 0 ? balance / unitPrice : int.MaxValue;

        var maxPossible = Math.Min(maxByRemaining, maxByMoney);
        if (maxPossible <= 0)
            return false;

        var actual = Math.Min(count, maxPossible);

        var totalPrice = checked(unitPrice * actual);
        if (!TryTakeCurrency(user, currency, totalPrice))
            return false;

        var spawned = 0;
        for (var i = 0; i < actual; i++)
            if (TrySpawnProduct(listing.ProductEntity, user))
                spawned++;
            else
                GiveCurrency(user, currency, unitPrice);

        if (spawned <= 0)
            return false;

        if (listing.RemainingCount > 0)
            listing.RemainingCount -= spawned;

        Sawmill.Info($"TryBuy: OK {listing.ProductEntity} x{spawned} for {unitPrice} {currency} each");
        return true;
    }


    public bool TrySell(string listingId, EntityUid machine, NcStoreComponent? store, EntityUid user, int count = 1)
    {
        if (store == null || store.Listings.Count == 0 || count <= 0)
            return false;

        var listing = store.Listings.FirstOrDefault(x => x.Id == listingId && x.Mode == StoreMode.Sell);
        if (listing == null)
            return false;

        if (!TryPickCurrencyForSell(store, listing, out var currency, out var unitPrice) || unitPrice <= 0)
            return false;

        var owned = GetOwned(user, listing.ProductEntity);
        var maxByRemaining = listing.RemainingCount >= 0 ? listing.RemainingCount : int.MaxValue;

        var maxPossible = Math.Min(owned, maxByRemaining);
        if (maxPossible <= 0)
            return false;

        var actual = Math.Min(count, maxPossible);

        if (!TryTakeProductUnits(user, listing.ProductEntity, actual))
            return false;

        var total = checked(unitPrice * actual);
        GiveCurrency(user, currency, total);

        if (listing.RemainingCount > 0)
            listing.RemainingCount -= actual;

        Sawmill.Info($"TrySell: OK {listing.ProductEntity} x{actual} for {unitPrice} {currency} each");
        return true;
    }


    public int GetOwned(EntityUid user, string productProtoId)
    {
        var total = 0;

        string? expectedStackType = null;

        if (_protos.TryIndex<EntityPrototype>(productProtoId, out var prodProto))
        {
            var stackName = _compFactory.GetComponentName(typeof(StackComponent));
            if (prodProto.TryGetComponent(stackName, out StackComponent? prodStackDef))
                expectedStackType = prodStackDef.StackTypeId;
        }

        foreach (var ent in EnumerateDeepItemsUnique(user))
        {
            if (expectedStackType != null &&
                _ents.TryGetComponent(ent, out StackComponent? stack) &&
                stack.StackTypeId == expectedStackType)
            {
                total += Math.Max(stack.Count, 0);
                continue;
            }

            if (_ents.TryGetComponent(ent, out MetaDataComponent? meta) &&
                meta.EntityPrototype?.ID == productProtoId)
                total += 1;
        }

        return total;
    }


    private bool TryTakeProductUnits(EntityUid user, string protoId, int amount)
    {
        if (amount <= 0)
            return true;

        string? stackType = null;

        if (_protos.TryIndex<EntityPrototype>(protoId, out var prodProto))
        {
            var stackName = _compFactory.GetComponentName(typeof(StackComponent));
            if (prodProto.TryGetComponent(stackName, out StackComponent? prodStackDef))
                stackType = prodStackDef.StackTypeId;
        }

        if (stackType == null)
        {
            foreach (var ent in EnumerateDeepItemsUnique(user))
            {
                if (!_ents.TryGetComponent(ent, out MetaDataComponent? meta) || meta.EntityPrototype?.ID != protoId)
                    continue;

                if (_ents.TryGetComponent(ent, out StackComponent? st))
                {
                    stackType = st.StackTypeId;
                    break;
                }
            }
        }

        if (stackType != null)
            return TryTakeCurrency(user, stackType, amount);

        var left = amount;
        foreach (var ent in EnumerateDeepItemsUnique(user))
        {
            if (left <= 0)
                break;

            if (!_ents.TryGetComponent(ent, out MetaDataComponent? meta) || meta.EntityPrototype?.ID != protoId)
                continue;

            if (_ents.EntityExists(ent))
            {
                _ents.DeleteEntity(ent);
                left -= 1;
            }
        }

        return left <= 0;
    }


    public bool TryExchange(
        string listingId,
        EntityUid machine,
        NcStoreComponent? store,
        EntityUid user,
        StoreExchangeListingBoundUiMessage msg
    ) =>
        false;

    private IEnumerable<EntityUid> EnumerateDeepItemsUnique(EntityUid owner)
    {
        var visited = new HashSet<EntityUid>();

        void Enqueue(EntityUid uid, Queue<EntityUid> queue)
        {
            if (visited.Add(uid))
                queue.Enqueue(uid);
        }

        var queue = new Queue<EntityUid>();

        if (_ents.TryGetComponent(owner, out InventoryComponent? inventory))
        {
            var slotEnum = new InventorySystem.InventorySlotEnumerator(inventory);
            while (slotEnum.NextItem(out var item))
                Enqueue(item, queue);
        }

        if (_ents.TryGetComponent(owner, out ItemSlotsComponent? itemSlots))
        {
            foreach (var slot in itemSlots.Slots.Values)
                if (slot.HasItem && slot.Item.HasValue)
                    Enqueue(slot.Item.Value, queue);
        }

        if (_ents.TryGetComponent(owner, out HandsComponent? hands))
        {
            foreach (var hand in hands.Hands.Values)
                if (hand.HeldEntity.HasValue)
                    Enqueue(hand.HeldEntity.Value, queue);
        }

        if (_ents.TryGetComponent(owner, out ContainerManagerComponent? cmcRoot))
        {
            foreach (var container in cmcRoot.Containers.Values)
            {
                foreach (var entity in container.ContainedEntities)
                    Enqueue(entity, queue);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;

            if (_ents.TryGetComponent(current, out ContainerManagerComponent? cmc))
            {
                foreach (var container in cmc.Containers.Values)
                {
                    foreach (var child in container.ContainedEntities)
                        Enqueue(child, queue);
                }
            }
        }
    }

    private bool TryTakeCurrency(EntityUid user, string stackType, int amount)
    {
        if (amount <= 0)
            return true;

        var cands = new List<(EntityUid Ent, int Count)>();
        var total = 0;

        foreach (var ent in EnumerateDeepItemsUnique(user))
            if (_ents.TryGetComponent(ent, out StackComponent? st) &&
                st.StackTypeId == stackType)
            {
                var cnt = Math.Max(st.Count, 0);
                if (cnt <= 0)
                    continue;

                cands.Add((ent, cnt));
                total += cnt;
            }

        if (total < amount)
            return false;

        cands.Sort((a, b) => a.Count.CompareTo(b.Count));

        var left = amount;
        foreach (var (ent, have) in cands)
        {
            if (left <= 0)
                break;

            var take = Math.Min(have, left);
            if (_ents.TryGetComponent(ent, out StackComponent? st))
            {
                var newCount = st.Count - take;
                _stacks.SetCount(ent, newCount, st);
                if (newCount <= 0 && _ents.EntityExists(ent))
                    _ents.DeleteEntity(ent);
            }

            left -= take;
        }

        return left <= 0;
    }


    private void GiveCurrency(EntityUid user, string stackType, int amount)
    {
        if (amount <= 0)
            return;

        if (!_protos.TryIndex<StackPrototype>(stackType, out var proto))
            return;

        foreach (var ent in EnumerateDeepItemsUnique(user))
        {
            if (amount <= 0)
                break;

            if (!_ents.TryGetComponent(ent, out StackComponent? st) || st.StackTypeId != stackType)
                continue;

            if (proto.MaxCount is { } max)
            {
                var canAdd = Math.Max(0, max - st.Count);
                if (canAdd <= 0)
                    continue;

                var add = Math.Min(canAdd, amount);
                _stacks.SetCount(ent, st.Count + add, st);
                amount -= add;
            }
            else
            {
                _stacks.SetCount(ent, st.Count + amount, st);
                amount = 0;
                break;
            }
        }

        if (amount <= 0)
            return;

        var coords = _ents.GetComponent<TransformComponent>(user).Coordinates;

        while (amount > 0)
        {
            var add = proto.MaxCount is { } maxPerStack
                ? Math.Min(amount, Math.Max(1, maxPerStack))
                : amount;

            var spawned = _ents.SpawnEntity(proto.Spawn, coords);

            if (_ents.TryGetComponent(spawned, out StackComponent? newStack))
                _stacks.SetCount(spawned, add, newStack);

            if (_ents.HasComponent<HandsComponent>(user))
                _hands.TryPickupAnyHand(user, spawned, false);

            amount -= add;
        }
    }


    private bool TrySpawnProduct(string protoId, EntityUid user)
    {
        try
        {
            var coords = _ents.GetComponent<TransformComponent>(user).Coordinates;
            var spawned = _ents.SpawnEntity(protoId, coords);
            if (_ents.HasComponent<HandsComponent>(user))
                _hands.TryPickupAnyHand(user, spawned, false);
            return true;
        }
        catch (Exception e)
        {
            Sawmill.Error($"Spawn failed for {protoId}: {e}");
            return false;
        }
    }
}
