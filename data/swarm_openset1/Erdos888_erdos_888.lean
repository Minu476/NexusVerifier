import FormalConjecturesUtil
import FormalConjectures.Subsets.FC100OpenSet1
open Filter
open scoped Classical in
open scoped Classical in
open scoped Classical in
open scoped Classical in

/-! Open-problem stub: Erdos888.erdos_888 → 'g_erdos_888_new' -/

namespace Erdos888

theorem g_erdos_888_new : (fun n : ℕ ↦ (Nat.findGreatest (p n) n : ℝ)) =Θ[atTop] (fun n : ℕ ↦ (n : ℝ) * Real.log (Real.log n) / Real.log n) := by
  sorry

end Erdos888
