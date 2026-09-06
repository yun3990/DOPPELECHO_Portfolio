using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// E키 단발 + E키 홀드 둘 다 지원.
/// 멀티플레이어: 대상에 NetworkIdentity가 있으면 NetworkInteraction Command로 중계.
/// 싱글플레이어 / NetworkIdentity 없음: 기존 직접 호출.
/// </summary>
public class InteractionSystem : MonoBehaviour
{
    [Header("레이캐스트 설정")]
    [SerializeField] private float interactRange = 2.5f;
    [SerializeField] private LayerMask interactLayer = ~0;

    [Header("참조")]
    [SerializeField] private Camera            playerCamera;
    [SerializeField] private InteractionUI     interactionUI;
    [Tooltip("임포스터 폼(3인칭) 전용 카메라. 할당 시 폼에 따라 레이 자동 교체")]
    [SerializeField] private ThirdPersonCamera thirdPersonCamera;

    public IInteractable     CurrentTarget     { get; private set; }
    public IHoldInteractable CurrentHoldTarget { get; private set; }

    private NetworkInteraction _netInteraction;
    private bool _wasHolding = false;
    // 실제로 OnHoldStart를 호출한 대상. 조준이 벗어나도 이 대상에 OnHoldEnd를 보장.
    private IHoldInteractable _heldTarget;

    // 인스펙터 참조가 없으면(런타임 스폰된 플레이어 프리팹) 씬 HUD 싱글톤으로 fallback
    private InteractionUI UI => interactionUI != null ? interactionUI : InteractionUI.Instance;

    private void Awake()
    {
        if (playerCamera == null)
            playerCamera = Camera.main;

        _netInteraction = GetComponentInChildren<NetworkInteraction>(true)
                       ?? GetComponentInParent<NetworkInteraction>();
    }

    private void Update()
    {
        // 설정 오버레이 등 UI 열림 → 상호작용 입력/안내 차단
        if (LocalInputLock.Blocked)
        {
            CurrentTarget     = null;
            CurrentHoldTarget = null;
            UI?.HidePrompt();
            return;
        }

        DetectTarget();
        HandleSingleInteract();
        HandleHoldInteract();
    }

    private Ray GetRay()
    {
        if (thirdPersonCamera != null && thirdPersonCamera.gameObject.activeInHierarchy)
            return thirdPersonCamera.GetAimRay();
        return new Ray(playerCamera.transform.position, playerCamera.transform.forward);
    }

    private void DetectTarget()
    {
        Ray ray = GetRay();

        if (Physics.Raycast(ray, out RaycastHit hit, interactRange, interactLayer))
        {
            CurrentTarget     = hit.collider.GetComponent<IInteractable>()
                             ?? hit.collider.GetComponentInParent<IInteractable>();
            CurrentHoldTarget = hit.collider.GetComponent<IHoldInteractable>()
                             ?? hit.collider.GetComponentInParent<IHoldInteractable>();

            // 안내 표시 설정 OFF면 프롬프트만 숨김 (상호작용 기능은 그대로 동작)
            bool showPrompts = Project.Settings.GameplaySettingsManager.Current.showInteractionPrompts;

            if (!showPrompts)
                UI?.HidePrompt();
            else if (CurrentHoldTarget != null)
                UI?.ShowPrompt(CurrentHoldTarget.InteractPrompt, CurrentHoldTarget.CanInteract);
            else if (CurrentTarget != null)
                UI?.ShowPrompt(CurrentTarget);
            else
                UI?.HidePrompt();

            return;
        }

        // 레이가 아무것도 못 맞춤 → 홀드 종료는 HandleHoldInteract가 _heldTarget 기준으로 처리
        CurrentTarget     = null;
        CurrentHoldTarget = null;
        UI?.HidePrompt();
    }

    private void HandleSingleInteract()
    {
        if (!Project.Settings.ControlBindingsManager.WasPressed(Project.Settings.GameAction.Interact)) return;

        LogRaycast();

        if (CurrentTarget == null || !CurrentTarget.CanInteract) return;
        if (CurrentHoldTarget != null) return;

        TryInteract(CurrentTarget);
    }

    /// <summary>E 입력 시 레이가 무엇을 맞췄는지 디버그 로그</summary>
    private void LogRaycast()
    {
        Ray ray = GetRay();
        if (Physics.Raycast(ray, out RaycastHit hit, interactRange, interactLayer))
        {
            var interactable = hit.collider.GetComponent<IInteractable>()
                            ?? hit.collider.GetComponentInParent<IInteractable>();
            var hold = hit.collider.GetComponent<IHoldInteractable>()
                    ?? hit.collider.GetComponentInParent<IHoldInteractable>();
            var ni = hit.collider.GetComponentInParent<Mirror.NetworkIdentity>();

            string targetName = (interactable as MonoBehaviour)?.gameObject.name
                             ?? (hold as MonoBehaviour)?.gameObject.name
                             ?? "(없음)";

            Debug.Log(
                $"[Interact] E → 콜라이더 '{hit.collider.name}' / 거리 {hit.distance:F2}m\n" +
                $"  IInteractable: {(interactable != null ? interactable.GetType().Name : "없음")} " +
                $"(CanInteract={interactable?.CanInteract})\n" +
                $"  IHoldInteractable: {(hold != null ? hold.GetType().Name : "없음")}\n" +
                $"  대상 오브젝트: {targetName} / netId: {(ni != null ? ni.netId.ToString() : "없음")}");
        }
        else
        {
            Debug.Log($"[Interact] E → 레이가 아무것도 맞추지 못함 (range {interactRange}m, layer {interactLayer.value})");
        }
    }

    private void HandleHoldInteract()
    {
        bool eHeld = Project.Settings.ControlBindingsManager.IsPressed(Project.Settings.GameAction.Interact);

        if (_wasHolding)
        {
            // 키를 뗐거나, 조준이 시작 대상에서 벗어났거나, 더 이상 상호작용 불가하면 종료.
            // 항상 시작했던 _heldTarget에 OnHoldEnd를 보내 _isCharging 등이 박제되지 않게 함.
            if (!eHeld || _heldTarget == null
                || CurrentHoldTarget != _heldTarget || !_heldTarget.CanInteract)
            {
                EndHold();
            }
            else
            {
                _heldTarget.OnHoldStay();
            }
            return;
        }

        // 새 홀드 시작
        if (eHeld && CurrentHoldTarget != null && CurrentHoldTarget.CanInteract)
        {
            HoldStart(CurrentHoldTarget);
            _heldTarget = CurrentHoldTarget;
            _wasHolding = true;
            _heldTarget.OnHoldStay();
        }
    }

    private void EndHold()
    {
        if (_heldTarget != null) HoldEnd(_heldTarget);
        _heldTarget = null;
        _wasHolding = false;
    }

    // ── 네트워크 중계 헬퍼 ────────────────────────────────────

    private void TryInteract(IInteractable target)
    {
        var mb    = target as MonoBehaviour;
        var netId = GetNetId(mb);

        if (netId.HasValue && _netInteraction != null)
        {
            // PickupItem은 CmdPickup, 나머지는 CmdInteract
            if (mb != null && (mb.GetComponent<PickupItem>() ?? mb.GetComponentInParent<PickupItem>()) != null)
                _netInteraction.CmdPickup(netId.Value);
            else
                _netInteraction.CmdInteract(netId.Value);
        }
        else
        {
            target.Interact();
        }
    }

    private void HoldStart(IHoldInteractable target)
    {
        var netId = GetNetId(target as MonoBehaviour);
        if (netId.HasValue && _netInteraction != null)
            _netInteraction.CmdHoldStart(netId.Value);
        else
            target.OnHoldStart();
    }

    private void HoldEnd(IHoldInteractable target)
    {
        if (target == null) return;
        var netId = GetNetId(target as MonoBehaviour);
        if (netId.HasValue && _netInteraction != null)
            _netInteraction.CmdHoldEnd(netId.Value);
        else
            target.OnHoldEnd();
    }

    /// <summary>MonoBehaviour에 NetworkIdentity가 있으면 netId 반환, 없으면 null</summary>
    private static uint? GetNetId(MonoBehaviour mb)
    {
        if (mb == null) return null;
        var ni = mb.GetComponent<Mirror.NetworkIdentity>()
              ?? mb.GetComponentInParent<Mirror.NetworkIdentity>();
        return ni != null ? ni.netId : (uint?)null;
    }

    private void OnDrawGizmos()
    {
        Ray ray = GetRay();
        Gizmos.color = (CurrentTarget != null || CurrentHoldTarget != null) ? Color.green : Color.yellow;
        Gizmos.DrawRay(ray.origin, ray.direction * interactRange);
    }
}