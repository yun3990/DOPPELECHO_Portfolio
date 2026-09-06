using UnityEngine;

/// <summary>
/// 전력 상태에 따라 오브젝트를 켜고 끄는 범용 컴포넌트.
///
/// 화면·안내판·표시 패널처럼 "전력이 없으면 꺼져 있어야 하는" 것에 붙인다.
/// 기능 스크립트를 수정하지 않고 붙이기만 하면 되므로, 수동 장치(밸브·레버)는 그대로 두고
/// 그 옆의 터미널 화면만 정전에 반응하게 만들 수 있다.
///
/// 발광 재질(LED/Emission)을 끄는 것은 PowerVisualOverride가 담당한다.
/// 이 컴포넌트는 오브젝트 활성화만 다루므로 둘을 같이 써도 된다.
///
/// 예)
///   밸브 미션 안내 터미널  → Active When Powered = [화면 캔버스]
///   게이트 레버 상태 패널  → Active When Powered = [statusText 부모]
///                            Active When Unpowered = [NO POWER 표시]
/// </summary>
public class PoweredObject : MonoBehaviour
{
    [Header("전력")]
    [Tooltip("비워두면 아무것도 하지 않는다")]
    [SerializeField] private PowerSystem powerSystem;

    [Tooltip("비상전력으로도 켜지는가.\n" +
             "해제하면 정상 전력에서만 켜진다 (터미널·화면은 보통 해제)")]
    [SerializeField] private bool emergencyCapable;

    [Header("대상")]
    [Tooltip("전력이 있을 때만 켜지는 오브젝트")]
    [SerializeField] private GameObject[] activeWhenPowered;

    [Tooltip("전력이 없을 때만 켜지는 오브젝트 (NO POWER 화면 등)")]
    [SerializeField] private GameObject[] activeWhenUnpowered;

    [Header("동작 (선택)")]
    [Tooltip("전력이 없을 때 함께 끌 컴포넌트 (Canvas, Light, ParticleSystem 등)")]
    [SerializeField] private Behaviour[] disableWhenUnpowered;

    private bool? _applied;

    private void OnEnable()
    {
        if (powerSystem != null) powerSystem.OnLevelChanged += OnLevelChanged;
        Apply();
    }

    private void OnDisable()
    {
        if (powerSystem != null) powerSystem.OnLevelChanged -= OnLevelChanged;
    }

    private void Start() => Apply();   // 늦게 붙어도 현재 단계를 반영

    private void OnLevelChanged(PowerLevel _) => Apply();

    private void Apply()
    {
        if (powerSystem == null) return;

        bool powered = emergencyCapable ? powerSystem.HasEmergencyPower : powerSystem.IsPowered;
        if (_applied == powered) return;
        _applied = powered;

        SetAll(activeWhenPowered,   powered);
        SetAll(activeWhenUnpowered, !powered);

        if (disableWhenUnpowered != null)
            foreach (var b in disableWhenUnpowered)
                if (b != null && b.enabled != powered) b.enabled = powered;
    }

    private static void SetAll(GameObject[] objects, bool active)
    {
        if (objects == null) return;

        foreach (var go in objects)
            if (go != null && go.activeSelf != active) go.SetActive(active);
    }
}
