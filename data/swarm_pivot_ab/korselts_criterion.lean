import FormalConjecturesUtil
import FormalConjectures.Subsets.FC100SolvedSet1
open scoped Nat
open scoped Classical in

/-! Stripped stub: AgohGiuga.korselts_criterion → proof reconstruction target 'g_korselts_criterion_new' -/

namespace AgohGiuga

theorem g_korselts_criterion_new (a : ℕ) (ha₁ : a.Composite) : IsCarmichael a ↔ Squarefree a ∧ ∀ p, p.Prime → p ∣ a → (p - 1 : ℕ) ∣ (a - 1 : ℕ) := by
  sorry

end AgohGiuga
