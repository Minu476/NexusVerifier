import FormalConjecturesUtil
import FormalConjectures.Subsets.FC100OpenSet1

/-! Open-problem stub: Erdos952.erdos_952 → 'g_erdos_952_new' -/

namespace Erdos952

theorem g_erdos_952_new : ∃ (x : ℕ → GaussianInt) (C : ℤ), Function.Injective x ∧ ∀ n, Prime (x n) ∧ (x (n + 1) - x n).norm < C := by
  sorry

end Erdos952
