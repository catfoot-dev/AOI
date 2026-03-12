using AoiDemo.Web.Models;

namespace AoiDemo.Web.Runtime;

/// <summary>
/// 이전 AOI 결과와 현재 AOI 결과를 비교해 entered/updated/left 집합을 계산합니다.
/// </summary>
public static class VisibilityDeltaComputer
{
    /// <summary>
    /// 두 visible id 집합의 차이를 계산해 증분 전송용 delta를 만듭니다.
    /// </summary>
    /// <param name="previousIds">이전 틱에서 보였던 엔티티 id 집합입니다.</param>
    /// <param name="currentIds">현재 틱에서 보이는 엔티티 id 집합입니다.</param>
    /// <returns>새로 들어온 id, 계속 보이는 id, 사라진 id를 나눈 delta 결과입니다.</returns>
    public static VisibilityDeltaIds Compute(IEnumerable<string> previousIds, IEnumerable<string> currentIds)
    {
        var previous = new HashSet<string>(previousIds, StringComparer.Ordinal);
        var current = new HashSet<string>(currentIds, StringComparer.Ordinal);

        var entered = current.Where(id => !previous.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var updated = current.Where(previous.Contains).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var left = previous.Where(id => !current.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        return new VisibilityDeltaIds(entered, updated, left);
    }
}
