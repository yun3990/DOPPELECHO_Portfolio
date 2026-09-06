/// <summary>
/// 상호작용 가능한 오브젝트 공통 인터페이스
/// 모든 상호작용 오브젝트는 이걸 구현
/// </summary>
public interface IInteractable
{
    /// <summary>크로스헤어에 표시될 텍스트</summary>
    string InteractPrompt { get; }

    /// <summary>상호작용 가능 여부 (잠김, 조건 미충족 등)</summary>
    bool CanInteract { get; }

    /// <summary>E키 눌렸을 때 호출</summary>
    void Interact();
}