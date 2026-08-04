import FormalConjecturesUtil
import FormalConjectures.Subsets.FC100OpenSet1
open Filter

/-! Open-problem stub: LittlewoodConjecture.padic_littlewood_conjecture → 'g_padic_littlewood_conjecture_new' -/

namespace LittlewoodConjecture

theorem g_padic_littlewood_conjecture_new (α : ℝ) (p : ℕ) (hp : p.Prime) : atTop.liminf (fun (n : ℕ) ↦ n * padicNorm p n * distToNearestInt (n * α)) = 0 := by
  sorry

end LittlewoodConjecture
