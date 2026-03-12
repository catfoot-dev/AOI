using AoiDemo.Web.Models;

namespace AoiDemo.Web.Aoi;

/// <summary>
/// 현재 월드 상태를 기준으로 플레이어별 AOI 가시성 결과를 계산하는 전략 계약입니다.
/// </summary>
public interface IAoiStrategy
{
    AoiAlgorithm Algorithm { get; }

    /// <summary>
    /// 월드 스냅샷을 읽어 각 플레이어가 볼 수 있는 엔티티 집합과 계산 메트릭을 만듭니다.
    /// </summary>
    /// <param name="snapshot">가시성 계산에 사용할 엔티티와 플레이어의 현재 상태입니다.</param>
    /// <returns>플레이어별 visible set과 인덱스/쿼리 비용을 담은 계산 결과입니다.</returns>
    AoiStrategyResult Compute(WorldSnapshot snapshot);
}
