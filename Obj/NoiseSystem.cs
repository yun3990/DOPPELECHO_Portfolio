using System;
using Mirror;
using UnityEngine;

/// <summary>
/// 게임플레이용 소리 한 건. AudioClip이나 실제 볼륨과는 무관한 순수 게임플레이 정보다.
/// </summary>
public readonly struct NoiseEvent
{
    /// <summary>소리가 난 월드 좌표.</summary>
    public readonly Vector3 position;

    /// <summary>소리의 세기. 같은 거리라면 큰 쪽이 우선한다(추후 우선순위용).</summary>
    public readonly float strength;

    /// <summary>이 소리가 닿는 반경 (m). 몬스터의 청력 배율이 여기에 곱해진다.</summary>
    public readonly float radius;

    /// <summary>
    /// 소리를 낸 주체. 타입을 특정하지 않는다 —
    /// 듣는 쪽은 INoiseSource / IHittable 같은 인터페이스로만 판단한다.
    /// </summary>
    public readonly object source;

    /// <summary>발생 시각 (NetworkTime 기준).</summary>
    public readonly double time;

    public NoiseEvent(Vector3 position, float strength, float radius, object source, double time)
    {
        this.position = position;
        this.strength = strength;
        this.radius   = radius;
        this.source   = source;
        this.time     = time;
    }
}

/// <summary>
/// "아직 소리를 내고 있는" 소음원. 소음 발생기처럼 계속 울리는 오브젝트가 구현한다.
///
/// 듣는 쪽(몬스터)은 이 인터페이스만 보고 판단하므로 특정 아이템을 알 필요가 없다.
/// 여기에 더해 IHittable까지 구현하면 몬스터가 기존 Damage 경로로 부술 수 있다.
///
/// 발소리·총성처럼 한 번 나고 마는 소리는 구현하지 않는다 (위치만 조사 대상이 된다).
/// </summary>
public interface INoiseSource
{
    /// <summary>현재 위치 (던져져서 움직였다면 그 위치).</summary>
    Vector3 NoisePosition { get; }

    /// <summary>지금도 소리를 내고 있는가. 꺼지거나 부서지면 false.</summary>
    bool IsMakingNoise { get; }
}

/// <summary>
/// 소리 이벤트 허브 — 어떤 오브젝트든 여기로 소리를 알리고, 듣는 쪽은 여기를 구독한다.
///
/// 서버 권위: Emit은 서버(또는 Mirror가 아예 돌지 않는 싱글플레이어)에서만 통과한다.
/// 클라이언트가 소리를 만들어 몬스터를 유인할 수 없다.
///
/// 확장: 걷기·달리기·착지·총성·근접 공격·문·보이스 채팅 등도 전부
///       NoiseSystem.Emit(위치, 세기, 반경, this) 한 줄이면 되고, 듣는 쪽은 손대지 않아도 된다.
/// </summary>
public static class NoiseSystem
{
    /// <summary>소리가 발생했을 때. 구독자는 스스로 거리·청력을 판단한다.</summary>
    public static event Action<NoiseEvent> OnNoise;

    /// <summary>소리를 낼 권한이 있는 쪽인가 (서버 또는 완전 오프라인).</summary>
    public static bool HasAuthority
        => NetworkServer.active || (!NetworkClient.active && !NetworkServer.active);

    /// <summary>소리 발생을 알린다. 서버가 아니면 조용히 무시된다.</summary>
    public static void Emit(Vector3 position, float strength, float radius, object source = null)
    {
        if (!HasAuthority) return;
        if (strength <= 0f || radius <= 0f) return;

        OnNoise?.Invoke(new NoiseEvent(position, strength, radius, source, Now));
    }

    private static double Now
        => NetworkServer.active || NetworkClient.active ? NetworkTime.time : Time.timeAsDouble;

    // 도메인 리로드를 끈 프로젝트에서 이전 플레이 세션의 구독이 남지 않도록 초기화한다
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => OnNoise = null;
}
