import FormalConjecturesUtil
import FormalConjectures.Subsets.FC100OpenSet1
open SimpleGraph

/-! Open-problem stub: WrittenOnTheWallII.GraphConjecture316.conjecture316 → 'g_conjecture316_new' -/

namespace WrittenOnTheWallII.GraphConjecture316

theorem g_conjecture316_new (G : SimpleGraph α) [DecidableRel G.Adj] (hG : G.Connected) (h : (averageDegree Gᶜ : ℚ) ≤ (pendantVertices G).card) : IsWellTotallyDominated G := by
  sorry

end WrittenOnTheWallII.GraphConjecture316
