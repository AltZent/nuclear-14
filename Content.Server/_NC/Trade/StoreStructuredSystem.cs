using System.Linq;
using Content.Server.Popups;
using Content.Shared._NC.Trade;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;


namespace Content.Server._NC.Trade;


public sealed class StoreStructuredSystem : EntitySystem
{
    private const float AutoCloseDistance = 3f;
    private const float CheckInterval = 0.2f;

    private static readonly ISawmill Sawmill = Logger.GetSawmill("ncstore");

    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly NcStoreLogicSystem _logic = default!;
    [Dependency] private readonly PopupSystem _popups = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;

    private TimeSpan _nextCheck = TimeSpan.Zero;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NcStoreComponent, ActivatableUIOpenAttemptEvent>(
            OnUiOpenAttempt,
            new[] { typeof(ActivatableUISystem), });

        SubscribeLocalEvent<NcStoreComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NcStoreComponent, RequestUiRefreshMessage>(OnUiRefreshRequest);
        SubscribeLocalEvent<AccessReaderComponent, AccessReaderConfigurationChangedEvent>(OnAccessReaderChanged);
        SubscribeLocalEvent<ContainerManagerComponent, EntInsertedIntoContainerMessage>(OnUserEntInserted);
        SubscribeLocalEvent<ContainerManagerComponent, EntRemovedFromContainerMessage>(OnUserEntRemoved);
        SubscribeLocalEvent<StackComponent, StackCountChangedEvent>(OnStackCountChanged);
    }


    private void OnUiOpenAttempt(EntityUid uid, NcStoreComponent comp, ref ActivatableUIOpenAttemptEvent ev)
    {
        ev.Cancel();

        var user = ev.User;
        if (!user.IsValid())
            return;

        if (!_ui.HasUi(uid, StoreUiKey.Key))
            return;

        if (!IsAccessAllowed(uid, comp, user))
        {
            _popups.PopupEntity(Loc.GetString("ncstore-no-access"), uid, user);
            return;
        }

        if (comp.CurrentUser is { } current && current != user)
        {
            _popups.PopupEntity(Loc.GetString("ncstore-busy"), uid, user);
            return;
        }

        if (TryComp(uid, out TransformComponent? storeXform) &&
            TryComp(user, out TransformComponent? userXform) &&
            !_xform.InRange(storeXform.Coordinates, userXform.Coordinates, AutoCloseDistance))
        {
            _popups.PopupEntity(Loc.GetString("ncstore-too-far"), uid, user);
            return;
        }

        comp.CurrentUser = user;

        if (!_ui.IsUiOpen(uid, StoreUiKey.Key, user))
            _ui.OpenUi(uid, StoreUiKey.Key, user);

        UpdateUiState(uid, comp, user);
    }


    private void OnUiClosed(EntityUid uid, NcStoreComponent comp, BoundUIClosedEvent ev)
    {
        if (ev.UiKey.Equals(StoreUiKey.Key))
            comp.CurrentUser = null;
    }

    private void OnUiRefreshRequest(EntityUid uid, NcStoreComponent comp, RequestUiRefreshMessage msg)
    {
        if (comp.CurrentUser is not { } user)
            return;

        if (!IsAccessAllowed(uid, comp, user))
        {
            _ui.CloseUi(uid, StoreUiKey.Key, user);
            comp.CurrentUser = null;
            return;
        }

        UpdateUiState(uid, comp, user);
    }

    public void UpdateUiState(EntityUid uid, NcStoreComponent comp, EntityUid user)
    {
        if (!IsAccessAllowed(uid, comp, user))
        {
            _ui.CloseUi(uid, StoreUiKey.Key, user);
            comp.CurrentUser = null;
            return;
        }

        var preferredCurrency = comp.CurrencyWhitelist.FirstOrDefault();
        var balance = string.IsNullOrEmpty(preferredCurrency) ? 0 : _logic.GetBalance(user, preferredCurrency);

        var listings = comp.Listings
            .Where(l => !string.IsNullOrEmpty(l.ProductEntity))
            .Select(l =>
            {
                string? currencyId = null;
                var priceF = 0f;

                if (!string.IsNullOrEmpty(preferredCurrency) && l.Cost.TryGetValue(preferredCurrency, out var vPref))
                {
                    currencyId = preferredCurrency;
                    priceF = vPref;
                }
                else
                {
                    var found = comp.CurrencyWhitelist.FirstOrDefault(c => l.Cost.ContainsKey(c));
                    if (!string.IsNullOrEmpty(found))
                    {
                        currencyId = found;
                        priceF = l.Cost[found];
                    }
                    else if (l.Cost.Count > 0)
                    {
                        var kv = l.Cost.First();
                        currencyId = kv.Key;
                        priceF = kv.Value;
                    }
                }

                var price = (int) MathF.Ceiling(priceF);
                var cat = l.Categories.Count > 0 ? l.Categories[0] : "Разное";

                int owned;
                try
                {
                    owned = _logic.GetOwned(user, l.ProductEntity);
                }
                catch
                {
                    owned = 0;
                }

                return new StoreListingData(
                    l.Id,
                    l.ProductEntity,
                    price,
                    cat,
                    currencyId ?? string.Empty,
                    l.Mode,
                    owned,
                    l.RemainingCount
                );
            })
            .ToList();

        const string readyCat = "Готово к продаже";

        var readyToSell = listings
            .Where(d => d.Mode == StoreMode.Sell && d.Owned > 0 && d.Remaining != 0)
            .Select(d => new StoreListingData(
                d.Id,
                d.ProductEntity,
                d.Price,
                readyCat,
                d.CurrencyId,
                d.Mode,
                d.Owned,
                d.Remaining))
            .ToList();

        if (readyToSell.Count > 0)
            listings.AddRange(readyToSell);

        _ui.SetUiState(uid, StoreUiKey.Key, new StoreUiState(balance, listings));
    }


    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextCheck)
            return;

        _nextCheck = _timing.CurTime + TimeSpan.FromSeconds(CheckInterval);

        var iter = EntityQueryEnumerator<NcStoreComponent, TransformComponent>();
        while (iter.MoveNext(out var uid, out var store, out var xform))
        {
            if (store.CurrentUser is not { } userUid)
                continue;

            if (!EntityManager.TryGetComponent(userUid, out TransformComponent? userXform))
            {
                store.CurrentUser = null;
                continue;
            }

            if (!_xform.InRange(xform.Coordinates, userXform.Coordinates, AutoCloseDistance))
            {
                _ui.CloseUi(uid, StoreUiKey.Key, userUid);
                store.CurrentUser = null;
                continue;
            }

            if (!IsAccessAllowed(uid, store, userUid))
            {
                _ui.CloseUi(uid, StoreUiKey.Key, userUid);
                store.CurrentUser = null;
                _popups.PopupEntity(Loc.GetString("ncstore-no-access"), uid, userUid);
            }
        }
    }

    private void OnAccessReaderChanged(
        EntityUid uid,
        AccessReaderComponent comp,
        ref AccessReaderConfigurationChangedEvent args
    )
    {
        if (TryComp<NcStoreComponent>(uid, out var store) && store.CurrentUser is { } user)
        {
            if (!IsAccessAllowed(uid, store, user))
            {
                _ui.CloseUi(uid, StoreUiKey.Key, user);
                store.CurrentUser = null;
            }
        }
    }


    private void OnUserEntInserted(
        EntityUid uid,
        ContainerManagerComponent comp,
        ref EntInsertedIntoContainerMessage args
    ) =>
        RefreshAllOpenStores();

    private void OnUserEntRemoved(
        EntityUid uid,
        ContainerManagerComponent comp,
        ref EntRemovedFromContainerMessage args
    ) =>
        RefreshAllOpenStores();


    private void OnStackCountChanged(
        EntityUid uid,
        StackComponent comp,
        ref StackCountChangedEvent args
    ) =>
        RefreshAllOpenStores();

    private void RefreshAllOpenStores()
    {
        var query = EntityQueryEnumerator<NcStoreComponent>();
        while (query.MoveNext(out var storeUid, out var storeComp))
            if (storeComp.CurrentUser is { } user)
                UpdateUiState(storeUid, storeComp, user);
    }


    private bool IsAccessAllowed(EntityUid storeUid, NcStoreComponent comp, EntityUid user)
    {
        if (TryComp<AccessReaderComponent>(storeUid, out var reader))
            return _access.IsAllowed(user, storeUid, reader);

        if (comp.Access is { Count: > 0, })
        {
            var fake = new AccessReaderComponent();
            fake.AccessLists.Clear();

            foreach (var group in comp.Access)
            {
                var set = new HashSet<ProtoId<AccessLevelPrototype>>();
                foreach (var token in group)
                {
                    if (_prototypeManager.TryIndex<AccessLevelPrototype>(token, out _))
                    {
                        set.Add(new(token));
                        continue;
                    }

                    if (_prototypeManager.TryIndex<AccessGroupPrototype>(token, out var grp))
                    {
                        if (grp.Tags.Count == 0)
                        {
                            Sawmill.Warning(
                                $"[Access] Empty access group '{token}' on {ToPrettyString(storeUid)}; skipping.");
                            continue;
                        }

                        if (set.Count > 0)
                        {
                            fake.AccessLists.Add(set);
                            set = new();
                        }

                        foreach (var lvl in grp.Tags)
                            fake.AccessLists.Add(new() { lvl, });

                        continue;
                    }

                    Sawmill.Warning(
                        $"[Access] Unknown access token '{token}' on {ToPrettyString(storeUid)}; skipping.");
                }

                if (set.Count > 0)
                    fake.AccessLists.Add(set);
            }

            if (fake.AccessLists.Count == 0)
            {
                Sawmill.Warning($"[Access] All access groups invalid/empty on {ToPrettyString(storeUid)}; denying.");
                return false;
            }

            return _access.IsAllowed(user, storeUid, fake);
        }

        return true;
    }
}
