import FormalConjecturesUtil
import FormalConjectures.Subsets.FC100OpenSet1
open Function

/-! Open-problem stub: Grimm.grimm_conjecture_weak → 'g_grimm_conjecture_weak_new' -/

namespace Grimm

theorem g_grimm_conjecture_weak_new (n k : ℕ) (hn : 1 ≤ n) (hk : 1 ≤ k) (h : ∀ i : Fin k, (n + i).Composite) : ∃ ps : Fin k ↪ ℕ, ∀ i : Fin k, (ps i).Prime ∧ ∃ j : Fin k, ps i ∣ (n + j) := by
  sorry

end Grimm
