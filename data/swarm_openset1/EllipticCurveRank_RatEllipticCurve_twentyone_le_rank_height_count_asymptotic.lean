import FormalConjecturesUtil
import FormalConjectures.Subsets.FC100OpenSet1
open EllipticCurveRank
open scoped WeierstrassCurve.Affine
open Module (finrank)
open WeierstrassCurve in
open scoped Topology
open Filter (atTop)
open _root_.WeierstrassCurve

/-! Open-problem stub: EllipticCurveRank.RatEllipticCurve.twentyone_le_rank_height_count_asymptotic → 'g_twentyone_le_rank_height_count_asymptotic_new' -/

namespace EllipticCurveRank.RatEllipticCurve

theorem g_twentyone_le_rank_height_count_asymptotic_new : ∃ f : ℕ → ℝ, atTop.Tendsto f (𝓝 0) ∧ ∀ H : ℕ, 1 < H → {E ∈ heightLE H | 21 ≤ E.rank}.ncard ≤ (H : ℝ) ^ f H := by
  sorry

end EllipticCurveRank.RatEllipticCurve
