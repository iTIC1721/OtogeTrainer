namespace OtogeTrainer.Core.Playback;

/// <summary>
/// 재생 엔진 추상화. (SPEC §9.2)
/// <para>
/// 구현은 libmpv(<c>OtogeTrainer.Playback</c>)이지만, 필요 시 LibVLCSharp 으로 교체할 수 있도록
/// 엔진 고유 개념(속성 이름, 커맨드 문자열)을 이 경계 밖으로 내보내지 않는다. (SPEC §4.2)
/// </para>
/// <para>모든 멤버는 UI 스레드에서 호출하고, 이벤트도 UI 스레드에서 발생한다.</para>
/// </summary>
public interface IMediaEngine : IDisposable
{
    /// <summary>엔진이 초기화되어 명령을 받을 수 있는가.</summary>
    bool IsReady { get; }

    /// <summary>엔진이 준비된 시점. 이미 준비됐으면 즉시 완료된다.</summary>
    Task WhenReady { get; }

    /// <summary>파일을 열고, 메타데이터(길이 등)를 읽을 수 있을 때 완료된다. 일시정지 상태로 열린다.</summary>
    Task LoadAsync(string path, CancellationToken cancellationToken = default);

    bool IsPaused { get; set; }

    /// <summary>현재 위치. 프레임 경계로 스냅된 실제 위치다. (M0-FINDINGS §5)</summary>
    TimeSpan Position { get; }

    TimeSpan? Duration { get; }

    /// <summary>컨테이너가 보고하는 프레임레이트. VFR 이거나 알 수 없으면 null.</summary>
    double? FrameRate { get; }

    long? FrameNumber { get; }

    /// <summary>재생 배속. 설정 시 <see cref="SpeedRules.Normalize"/> 가 적용된다.</summary>
    double Speed { get; set; }

    bool PitchCorrection { get; set; }

    /// <summary>오디오 지연(ms). 양수 = 소리를 늦춤. (FR-402)</summary>
    int AudioDelayMs { get; set; }

    /// <summary>프레임 정확 절대 시크. (FR-306)</summary>
    void SeekAbsolute(TimeSpan position);

    /// <summary>프레임 정확 상대 시크. (FR-306)</summary>
    void SeekRelative(TimeSpan delta);

    /// <summary>정확히 한 프레임 이동. 자동으로 일시정지된다. (FR-301)</summary>
    void StepFrame(int direction);

    /// <summary>현재 프레임을 파일로 저장한다. OSD 없이 원본 해상도.</summary>
    Task<bool> SaveFrameAsync(string path);

    /// <summary>
    /// 파일이 열려 메타데이터를 읽을 수 있게 된 시점.
    /// 끝에 도달하면 마지막 프레임에서 일시정지 상태로 머문다(keep-open).
    /// </summary>
    event EventHandler? FileLoaded;
}
