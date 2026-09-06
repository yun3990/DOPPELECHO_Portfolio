using Mirror;
using UnityEngine;

/// <summary>
/// 상호작용 Command 중계.
/// 서버에서 거리 + CanInteract 검증 후 실행.
/// PickupItem은 CmdPickup으로 별도 처리.
/// </summary>
public class NetworkInteraction : NetworkBehaviour
{
    [SerializeField] private float maxInteractDistance = 3.5f;

    private NetworkInventorySync _netInvSync;

    private void Awake()
    {
        _netInvSync = GetComponentInChildren<NetworkInventorySync>(true)
                   ?? GetComponentInParent<NetworkInventorySync>();
    }

    /// <summary>PickupItem 전용 Command — 아이템을 월드에서 제거하고 인벤토리에 추가</summary>
    [Command]
    public void CmdPickup(uint netId)
    {
        if (!NetworkServer.spawned.TryGetValue(netId, out NetworkIdentity identity)) return;

        var pickup = identity.GetComponent<PickupItem>()
                  ?? identity.GetComponentInChildren<PickupItem>(true);
        if (pickup == null || !pickup.CanInteract) return;

        if (ServerDistanceTo(identity) > maxInteractDistance) return;

        string itemName = pickup.data?.itemName ?? "";
        int    count    = pickup.count;

        // ⚠ 인벤토리는 아이템을 "이름"으로 되살린다. 레지스트리에 없으면 월드에서 지우는 순간
        //    영영 사라진다(손에도 안 들리고 가방에도 안 들어감). 지우기 전에 먼저 확인한다.
        if (ItemRegistry.Find(itemName) == null)
        {
            Debug.LogError(
                $"[Pickup] '{itemName}'이(가) ItemRegistry에 등록되어 있지 않습니다. " +
                $"씬의 ItemRegistry → All Items 배열에 이 ItemData를 추가하세요. 줍기를 취소합니다.\n" +
                $"현재 등록된 아이템: {ItemRegistry.RegisteredNames}",
                pickup);
            return;
        }

        NetworkServer.Destroy(pickup.gameObject);  // 모든 클라이언트에서 제거

        _netInvSync?.ServerAddItem(itemName, count);
    }

    /// <summary>
    /// 서버 거리 검증용 거리. 오브젝트 피벗이 아니라 콜라이더 경계까지의 거리를 잰다.
    ///
    /// 큰 오브젝트(포드·박스 등)는 피벗이 멀리 있어, 표면에 붙어 있어도 피벗 기준으로는
    /// 거리 초과로 거부된다(클라이언트 레이캐스트는 표면을 맞히므로 서버와 판정이 어긋남).
    /// bounds 기준이라 모든 콜라이더 타입(비볼록 MeshCollider 포함)에서 안전하다.
    /// </summary>
    private float ServerDistanceTo(NetworkIdentity identity)
    {
        Vector3 pos = transform.position;
        float best = Vector3.Distance(pos, identity.transform.position);

        foreach (var col in identity.GetComponentsInChildren<Collider>(false))
        {
            if (col == null || col.isTrigger) continue;
            best = Mathf.Min(best, Vector3.Distance(pos, col.bounds.ClosestPoint(pos)));
        }
        return best;
    }

    /// <summary>
    /// CmdInteract 실행 중 서버에서 호출자의 NetworkInventorySync 접근용.
    /// LargeBatterySlot 등 인벤토리 소비 오브젝트가 올바른 플레이어 인벤토리를 참조하기 위해 사용.
    /// </summary>
    public static NetworkInventorySync CurrentInteractorInventory { get; private set; }

    /// <summary>
    /// CmdInteract 실행 중 호출자(플레이어)의 NetworkInteraction.
    /// EscapePod 등에서 상호작용한 플레이어의 NetworkPlayerState를 찾을 때 사용.
    /// </summary>
    public static NetworkInteraction CurrentInteractor { get; private set; }

    [Command]
    public void CmdInteract(uint netId)
    {
        if (!NetworkServer.spawned.TryGetValue(netId, out NetworkIdentity identity))
        {
            Debug.LogWarning($"[Server][Interact] netId {netId} 스폰 목록에 없음");
            return;
        }

        float dist = ServerDistanceTo(identity);
        if (dist > maxInteractDistance)
        {
            Debug.LogWarning($"[Server][Interact] 거리 초과: {dist:F2}m > {maxInteractDistance}m " +
                             $"오브젝트='{identity.name}' netId={netId}");
            return;
        }

        CurrentInteractor          = this;
        CurrentInteractorInventory = GetComponentInChildren<NetworkInventorySync>(true)
                                  ?? GetComponentInParent<NetworkInventorySync>();

        var target = identity.GetComponent<IInteractable>()
                  ?? identity.GetComponentInChildren<IInteractable>(true);
        if (target == null)
        {
            Debug.LogWarning($"[Server][Interact] {identity.name}에서 IInteractable 못 찾음");
            CurrentInteractor = null;
            CurrentInteractorInventory = null;
            return;
        }

        if (!target.CanInteract)
        {
            Debug.LogWarning($"[Server][Interact] {target.GetType().Name}.CanInteract=false " +
                             $"(서버 손 아이템: {CurrentInteractorInventory?.ServerHandItem?.itemName ?? "없음"})");
            CurrentInteractor = null;
            CurrentInteractorInventory = null;
            return;
        }

        Debug.Log($"[Server][Interact] {target.GetType().Name}.Interact() 실행 " +
                  $"(서버 손 아이템: {CurrentInteractorInventory?.ServerHandItem?.itemName ?? "없음"})");
        target.Interact();
        CurrentInteractor = null;
        CurrentInteractorInventory = null;
    }

    [Command]
    public void CmdHoldStart(uint netId)
    {
        if (!NetworkServer.spawned.TryGetValue(netId, out NetworkIdentity identity)) return;

        if (ServerDistanceTo(identity) > maxInteractDistance) return;

        var hold = identity.GetComponent<IHoldInteractable>()
                ?? identity.GetComponentInChildren<IHoldInteractable>(true);
        if (hold == null) return;

        var interactable = identity.GetComponent<IInteractable>()
                        ?? identity.GetComponentInChildren<IInteractable>(true);
        if (interactable != null && !interactable.CanInteract) return;

        // 홀드 대상도 "누가 눌렀는지"를 알아야 하는 경우가 있다 (카드키 검증 등).
        // CmdInteract와 같은 방식으로 호출자를 잠깐 노출한다.
        CurrentInteractor = this;
        hold.OnHoldStart();
        CurrentInteractor = null;
    }

    [Command]
    public void CmdHoldEnd(uint netId)
    {
        if (!NetworkServer.spawned.TryGetValue(netId, out NetworkIdentity identity)) return;
        var hold = identity.GetComponent<IHoldInteractable>()
                ?? identity.GetComponentInChildren<IHoldInteractable>(true);

        CurrentInteractor = this;
        hold?.OnHoldEnd();
        CurrentInteractor = null;
    }

    [Command]
    public void CmdDealDamage(uint victimNetId, float damage, int damageType)
    {
        if (!NetworkServer.spawned.TryGetValue(victimNetId, out NetworkIdentity identity)) return;
        // 자기 자신 공격 차단 (netIdentity 비교 + netId 이중 보호)
        if (identity == netIdentity || victimNetId == netId)
        {
            Debug.LogWarning($"[Server][Damage] 자기 자신 공격 차단: netId={victimNetId}");
            return;
        }
        // 플레이어는 PlayerHealth로 직접 (부위 배수는 공격자 측에서 이미 적용됨 → Hitbox 재적용 방지)
        var health = identity.GetComponentInChildren<PlayerHealth>(true);
        if (health != null)
        {
            health.OnHit(default, damage, (DamageType)damageType);
            return;
        }

        // 그 외(전기함, 몬스터 등)
        var hittable = identity.GetComponent<IHittable>()
                    ?? identity.GetComponentInChildren<IHittable>(true);
        if (hittable == null) return;

        // 임포스터 전용 대상(전기함 등): 공격자가 임포스터가 아니면 차단
        if (hittable is IImposterOnly && !AttackerIsImposter())
        {
            Debug.Log($"[Server][Damage] {identity.name}은(는) 임포스터만 파괴 가능 — 공격 차단");
            return;
        }

        // 대상이 공격자의 장비를 조회할 수 있도록 노출 (픽 보너스 등)
        CurrentAttackerInventory = _netInvSync;
        hittable.OnHit(default, damage, (DamageType)damageType);
        CurrentAttackerInventory = null;
    }

    /// <summary>
    /// CmdDealDamage 실행 중 서버에서 공격자의 인벤토리 접근용.
    /// 기계팔 잠금부처럼 "무슨 무기로 때렸는지"에 따라 피해량이 달라지는 대상이 사용한다.
    /// (CurrentInteractorInventory와 같은 패턴 — 해당 호출 동안만 유효)
    /// </summary>
    public static NetworkInventorySync CurrentAttackerInventory { get; private set; }

    /// <summary>이 Command를 호출한 공격자가 임포스터인지 (PlayerColorIdentity 기준)</summary>
    private bool AttackerIsImposter()
    {
        var colorId = GetComponentInChildren<PlayerColorIdentity>(true)
                   ?? GetComponentInParent<PlayerColorIdentity>();
        return colorId != null && colorId.IsImposter;
    }
}
