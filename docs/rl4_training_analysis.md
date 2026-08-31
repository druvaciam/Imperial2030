# RL-4: why RL-3 plays nonsense, and what to change

**Evidence:** two exported games (`new-rel_d57574a3…json`, 749 actions; `python_rl/RL-3_worst_game.json`,
647 actions) **plus 12 freshly generated games** via `Tests/RLPerNationBehaviourTests.cs`, which exists
because the two exported games alone supported a conclusion that the larger sample then refuted.
**Date:** 2026-08-26

---

## 0. What is measured vs what is inferred

This document mixes both, so they are labelled. **[MEASURED]** = computed from the exported game, the
saved model, or read directly from the code. **[HYPOTHESIS]** = a proposed explanation consistent with
the measurements but not proven by them.

An earlier draft of this file asserted that RL-3 was "a checkpoint of a regressing run". That was wrong:
it came from a stale comment in `train.py` describing the state at ~8.4M steps, not from the model. The
model reports **68,515,000 steps** and a best mean reward of **+126.94**. The corrected §3c is built
from the artifact instead, and reaches a sharper conclusion.

---

## 1. What actually happened

Bot Charlie (RL-3) ruled India for 13 rondel moves. Its slot distribution against everyone else's:

| player | moves | Maneuver | Production | Import | Taxation | Factory | Investor |
|---|---:|---:|---:|---:|---:|---:|---:|
| druvaciam (human) | 30 | 27% | 13% | 3 | 8 | 3 | 4 |
| Bot Alpha **(RL-3)** | 20 | 20% | **35%** | 2 | 3 | 2 | 2 |
| Bot Delta (RL) | 20 | 20% | 35% | 3 | 2 | 3 | 1 |
| Bot Echo (Default) | 19 | 32% | 26% | 2 | 3 | 2 | 1 |
| Bot Bravo (RL-2) | 17 | 29% | 41% | 1 | 1 | 2 | 1 |
| **Bot Charlie (RL-3)** | **13** | **54%** | **0%** | **0** | 2 | 2 | 2 |

Charlie spent **54% of its turns on Maneuver and never produced or imported a single unit.** India had
zero units the entire time it ruled — confirmed two ways: no `Production`/`Import` action for nation 2
appears until index 455, *after* Bot Bravo took over; and Charlie logged **14 `AutoEndPhase` entries**,
exactly 7 Maneuver visits × (Fleets + Armies), meaning every maneuver auto-skipped both phases because
there was nothing to move.

Seven wasted turns out of thirteen. That is the "complete nonsense".

## 2. What replicates, and what did not

The single-game reading of this was **wrong**, and testing it is what showed that.

### The hypothesis that failed **[TESTED, REFUTED]**

Two exported games showed RL-3 playing well as Russia and badly as India, which suggested the cause was
the state vector's **fixed** nation ordering (`foreach (var nation in imperial2030Nations)` — Russia's
features always at block 0, India's always at block 2, with no weight sharing between blocks and no
egocentric rotation, unlike players which *are* reordered). Plausible, and the ordering is real.

`Tests/RLPerNationBehaviourTests.cs` runs the live RL-3 policy across 12 games and reports the rondel
distribution split by controlled nation:

| nation | games | moves | Prod | Maneuver | Factory | Tax | Investor | Import | **built/max** |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Russia | 3 | 52 | 21% | 27% | 2% | 21% | 19% | 10% | **1/6** |
| China | 3 | 19 | 26% | 26% | 0% | 21% | 26% | 0% | **0/6** |
| India | 5 | 38 | **24%** | 29% | 3% | 18% | 24% | 3% | **1/10** |
| Brazil | 3 | 49 | 33% | 18% | 4% | 20% | 20% | 4% | **2/6** |
| USA | 4 | 48 | 21% | 27% | 6% | 19% | 23% | 4% | **2/8** |
| Europe | 2 | 20 | 30% | 15% | 0% | 30% | 20% | 5% | **0/4** |

**India averages 24% Production and 29% Maneuver — completely normal.** There is no per-nation collapse.
Bot Charlie's 54%-Maneuver / 0%-Production stint was a 13-move sample; §1 already put that at ~2%
probability under the other bots' rates, and across the several RL-3 nation-stints in those two games,
seeing one such outlier is unremarkable. It was variance, not a structural defect.

The nation-ordering issue in `GetStateVector` is still real as *code*, and rotating it is still cheap
and defensible — but it is **not** the cause of what you saw, and it should not be sold as the headline
fix.

### The pathology that does replicate **[MEASURED]**

Two, and they appear in **every** nation:

**Factory is never chosen — and the right way to count that is not a percentage.**

An earlier draft of this section reported "Factory 0.8% of moves, versus 12.5% uniform and 10-15% for the
opponents" and set 10-15% as the RL-4 target. **That target was wrong**, and the rulebook says why.
A nation has four home cities and may hold one factory in each (p.7, *"Only one factory may be built in
each city"*), and **two are already built at setup** (p.4, *"Each nation starts with two factories"*).
So a nation normally gets **two builds per game**, after which the Factory slot does nothing.

Not a hard ceiling, and an earlier version of this paragraph wrongly said it was: three armies can destroy
a foreign factory (p.11, *"The foreign factory and the three armies are removed from the game board"*),
which frees that city to be built in again — the rulebook never says a destroyed factory leaves the game,
p.4 describes unbuilt factories as a supply "placed next to the game board", and `GetFactoryBuildOptions`
gates only on `!HasFactory`, so the engine allows the rebuild. It is uncommon rather than impossible: one
destruction across the whole 260-move exported game.

Either way a slot *frequency* has no meaningful target — roughly two useful landings are available per
nation-game however many rondel moves that nation gets, so the percentage mostly measures game length.
The 10-15% the opponents show is not a policy rate; it is *two landings* spread over ~20 moves.

`RLPerNationBehaviourTests` now measures **factories built per nation-stint** against that ceiling of 2:

> **RL-3: 6 factories in 20 nation-stints — 0.30 per stint. Every other bot: 123 in 80 — 1.54 per stint.**

The opponents run at **77% of the achievable maximum**; RL-3 runs at **15%**, a 5x gap on the metric the
rules actually bound. Two of its six nations built nothing at all across every game they appeared in.
This corroborates an observation already recorded in `TcpTrainingServer.cs`: RL-3 built "zero factories in
4 of 6 worst-loss test games while every single opponent bot built at least one in all 6." The per-game
exports agree — in `RL-3_worst_game.json` the RL bot took **0 Factory landings in 31 moves across India,
Brazil and USA**, while all five opponents built every factory they could.

Worth noting against the original complaint: in the game you flagged, **Bot Charlie built both of India's
factories** (2/2 in 13 moves). The single game that started this was not the failure case.

**Investor is over-chosen: 19-26% everywhere.** Unlike Factory, Investor has no supply cap, so a
frequency *is* readable here - but it is only meaningful as the other half of the pairing below, not as a
deviation from a uniform baseline.

That pairing is the whole story. Factories are the engine of the game — each one is 2M of tax revenue
every Taxation turn and a free unit every Production turn, for the rest of the game (p.7, p.12). Investor
is immediate cash. **The agent has learned to take the immediate payout and never build the thing that
compounds** — which is precisely what §3a and §3b predict a short-horizon, shaping-dominated objective
would produce, and it is far better evidence for those than the nation story was.

It also explains Charlie's Maneuver-spam as a *symptom*: a nation that never builds has few units and
little to do, so the remaining slots absorb its turns.

The comment history in `TcpTrainingServer.cs` records the likely origin: an earlier Factory penalty was
strong enough that "the prior magnitude taught the agent to just avoid the Factory slot outright", and
was halved to `-8` in response. **The halving did not undo the learned aversion** — 0.30 builds per
nation-stint against an achievable 2 says the agent still will not go near it.

## 3. Three other things working against you

### 3a. The discount horizon is NOT too short **[TESTED, REFUTED]**

`gamma=0.995`, confirmed in the saved model. The earlier draft flagged this as a likely problem while
explicitly noting *"I did not measure the actual episode length"* and that it "should be measured before
acting on it". It has now been measured, from the 8,646 `rollout/ep_len_mean` samples RL-3 already logged
to `tb_logs/RL-3_0` (steps 1.4M -> 68.6M):

| | steps per episode |
|---|---:|
| min | 44 |
| mean | 58 |
| max | 70 |
| last 200 samples | 61.3 |

**Episodes are ~61 agent steps, not the 200+ the concern assumed.** At `gamma=0.995` the half-life is 138
steps, so the terminal win/loss reward arrives with **73% of its value intact** (`0.995^61 = 0.735`).
Raising gamma to 0.999 would take that to 94% — a modest gain, not a fix. The doc's own criterion was "if
episodes are ~150 steps the concern largely evaporates"; at 61 it evaporates entirely.

**gamma stays at 0.995.** The measurement is recorded next to both `gamma=` sites in `train.py` so this
is not re-litigated. What remains true is §3b — the imbalance is between shaping and terminal *magnitude*,
not between the horizon and the episode.

### 3b. Shaping rewards are large, dense and immediate; the goal is small, sparse and distant

The reward function now carries `-80/-40/-30/-25/-20/-13/-10/-8/-5/-2` shaping terms plus `+10` per
factory. With `VecNormalize(norm_reward=True)` rescaling by a running std dominated by those frequent
terms, the terminal signal is compressed further still. An agent that learns "never get penalised" scores
better than one that learns "win", which is what the log shows.

**A defect found while acting on this [MEASURED].** `HandleStepAsync` folded `explicitBonusReward` into
`reward` at the top of the function and then kept subtracting from it for another sixty lines. The two
Investor penalties down there — up to **-80** for personally covering a nation's interest shortfall, and
**-20** for missing one's own interest — were computed, logged as `[RL PENALTY]`, and discarded. **They
never reached the agent, for the whole of RL-3's 68.5M steps.** So the largest shaping term named above
was never actually in the objective, and the training logs asserted otherwise. Fixed by folding shaping in
exactly once, after every term is computed; `Tests/TrainingRewardCurriculumTests` guards the ordering
(verified to fail against the original arrangement). Note the consequence for RL-4: it will train against
a materially harsher Investor penalty than RL-3 ever did.

### 3c. The entropy schedule has been pinned at its floor for the last 48 million steps

Read from `python_rl/RL-3.zip` itself, not inferred:

| field | value |
|---|---|
| `num_timesteps` | **68,515,000** |
| `_num_timesteps_at_start` | 51,865,000 |
| `_total_timesteps` | 71,865,000 |
| `ent_coef` | **0.015** — the configured floor |
| `gamma` | 0.995 |
| best mean reward (`best_reward.txt`) | **+126.94** |

RL-3 is a well-trained model — 68.5M steps, and its own metric is strongly positive. But
`EntCoefScheduleCallback` computes `progress = self.num_timesteps / self.total_timesteps` using the
**cumulative** counter against a fixed `TOTAL_TIMESTEPS = 20_000_000`. At 68.5M that is `3.4`, clamped
to `1.0`:

    cumulative  20,000,000 -> progress 1.00 -> ent_coef 0.0150
    cumulative  68,515,000 -> progress 1.00 -> ent_coef 0.0150

**Entropy has been at the 0.015 floor since step 20M — the last 48.5 million steps trained at minimum
exploration.** The saved `ent_coef = 0.015` confirms it.

The learning-rate schedule does *not* have this problem: SB3 derives `progress_remaining` from the
current `learn()` call's window, so with `_num_timesteps_at_start = 51.865M` and
`_total_timesteps = 71.865M` the LR gets fresh runway on every resume. Only the hand-rolled entropy
callback uses the raw cumulative counter, and only it is stuck.

That matters precisely for §2. A sub-policy that is already weak (the India nation-block) cannot recover
without exploration, and exploration was switched off for the majority of training. The good blocks
keep getting reinforced; the starved one has no mechanism to improve.

**And the +126.94 best mean reward is the other half of the story.** By its own objective this run is
succeeding, while one of its nations plays 54% Maneuver with zero units. That is the clearest possible
evidence that the reward function is measuring the wrong thing (§3a/§3b): dense shaping is being
optimised well, and per-nation competence is invisible to the metric.

Smaller notes: `learning_rate` 6e-5 -> 2e-5 is low for a policy that still needs to change behaviour;
`clip_obs=10.0` is dead config given `norm_obs=False`; and `check_env` is imported but never called.

## 4. Representing the map properly — the actual research

Your instinct is right: a 3172-float flat vector is the wrong shape for a board that is fundamentally a
**graph** of territories with adjacency, sea connections, canals and rail. Two established families
apply, and they compose.

### Graph neural networks over the territory graph

Model the board as `G = (V, E)`: `V` = territories (each with features — owner flag, units by
type/hostility, factory, city type, home nation), `E` = the real adjacency from
`Shared/Constants/MapConnectivity.cs`, optionally typed (land / sea / canal / rail). A GNN then computes
each territory's representation from its neighbourhood, so "threatened", "cut off", "reachable" become
*learned* features rather than things the MLP must infer from unrelated indices.

This is well-trodden for board games specifically. GNNs "operate on graph representations instead of
grid representations/images and can model complicated task-specific structure and relational inductive
biases" — the [Hex/DQN-GNN study](https://arxiv.org/pdf/2311.13414) tests exactly this substitution on a
connection game, and a [GCN+DCNN hybrid on Go](https://www.researchgate.net/publication/358661169_Graph_Convolutional_Networks_for_Turn-Based_Strategy_Games)
outperformed the plain CNN baseline. The
[comprehensive DRL+GNN review](https://arxiv.org/pdf/2206.07922) covers the integration patterns and
notes GNNs support "dynamic action spaces and varying input sizes" — directly relevant, since your
action space is currently padded to fixed territory slots. The
[ICLR 2026 practical guide](https://iclr-blogposts.github.io/2026/blog/2026/rl-with-gnns/) is the most
implementation-oriented starting point.

### Entity encoders with attention (the AlphaStar pattern)

Zambaldi et al. (2018) introduced multi-head dot-product attention over structured observations, and
**AlphaStar** adopted it: entities are encoded through a three-layer Transformer, and the resulting
embeddings are merged with scalar and map features into a joint representation
([survey](https://arxiv.org/html/2301.03044v3),
[AlphaStar analysis](https://www.alexirpan.com/2019/02/22/alphastar-part2.html)). The property you want
is **permutation invariance**: the same weights process every entity, so learning about one transfers to
all of them.

Applied here, the six nations become *entities*, not fixed offsets — which is the direct fix for §2.
[Entity-based RL for cyber defence](https://arxiv.org/html/2410.17647v3) demonstrates the pattern
generalising across differently-sized environments.

**The cheap 80% of this, available without any architecture change:** rotate the nation blocks so the
**acting nation is always block 0** and the others follow in fixed clockwise order from it. You already
do exactly this for players. It is a handful of lines in `GetStateVector`, it makes the encoding
egocentric, and it collapses six sub-policies into one. It also breaks state-vector compatibility, so it
is an RL-4-only change — which is fine, because you are retraining anyway.

## 5. Recommended plan for RL-4

Reordered after the §2 test. The measured problem is **Factory avoidance (0.30 of 2 per nation) plus Investor
over-selection (23-35%)** in every nation — an agent taking the immediate payout and never building the
compounding asset. Everything below targets that.

**1. Fix `EntCoefScheduleCallback`. [DONE]** It divides the *cumulative* `num_timesteps` by a fixed
`TOTAL_TIMESTEPS = 20_000_000`, so at 68.5M it clamps to 1.0 and `ent_coef` has sat at the 0.015 floor
since step 20M. Measure progress over the current run's window instead —
`(num_timesteps - _num_timesteps_at_start) / (this run's budget)` — the way SB3's own LR schedule
already does. This is an outright bug, it affects every future resume, and **a learned aversion cannot
be unlearned without exploration**, which makes it a prerequisite for everything else.

*Implemented* as `RunRelativeSchedule`, which anchors on `num_timesteps` at training start so each resumed
run gets its own full schedule. Verified against RL-3's actual resume point: entropy now runs
0.0500 -> 0.0325 -> 0.0150 across a 20M run where the old code returned a flat 0.0150 throughout.

**2. Attack the Factory aversion directly. [DONE]** The `-8` "wasted Factory action" penalty was already halved
once, from -15/-10, because the original "taught the agent to just avoid the Factory slot outright". At
0.30-of-2 the aversion clearly survived the halving. Options, cheapest first:
   - Raise the `+10` build reward rather than lowering the penalty further, so *attempting* has positive
     expected value under uncertainty.
   - Suppress the wasted-Factory penalty entirely for the first N million steps, so the agent can
     discover what a factory pays back before it learns to fear the slot.

   *Implemented, both of them.* The build reward is now `FactoryBuildReward = 16` (was 10): a wasted
   landing costs 8, or 13 once every city is built, so at +10 visiting Factory needed better than a 57%
   chance of being buildable just to break even — and after a nation's usual two builds that chance drops
   to near zero for the rest of the episode, absent an enemy destroying one of its factories. At 16 the
   break-even is ~45%. Separately, a new `factoryPenaltyScale`
   holds both Factory penalties at **0 for the first 15% of a run**, ramping to full by 40%, so the payoff
   is discovered before the trap is taught.
   - Verify with the §2 harness's `built/max` column and its per-stint line — **not** with a slot
     percentage, which the two-per-nation supply cap makes meaningless. Target: **≥1.5 factories built
     per nation-stint**, matching the 1.54 the heuristic bots achieve against a ceiling of 2.

**3. ~~Anneal γ upward~~ — measured, not needed. [DONE: measured, no change made]** The plan said to
measure episode length before acting on this. Measured: **~61 agent steps per episode** (§3a), against a
138-step half-life at `gamma=0.995`, so the terminal reward already arrives at 73% strength. The horizon
is not the problem and γ is unchanged; the measurement is now recorded in `train.py` beside both `gamma=`
settings so the idea is not re-adopted on intuition later. OpenAI Five's γ annealing remains the right
reference *if* episode length ever grows — it is simply not what is wrong here.

**4. Decay shaping magnitude over training. [DONE]** The `-80/-40/-30` terms dominate an objective whose
terminal signal is a discounted ±100. The end state should be that the VP margin is the strongest
signal.

*Implemented* as `shapingScale`, held at 1.0 for the first half of a run then decaying linearly to 0.30.
It multiplies shaping only: the final VP margin and the flat ±100 are applied after the fold point and are
deliberately never scaled, so decaying shaping makes winning relatively **stronger**. A test asserts that
ordering.

Both scales are sent per episode over the existing TCP protocol (`shapingScale` / `factoryPenaltyScale` on
`reset`), driven by `CurriculumCallback` in `train.py`. Both are nullable on the wire and default to 1.0,
so an older `imperial_env.py` that never sends them trains on exactly the historical reward function.

**5. Train from scratch (`--reset`).** RL-3 is well-trained (68.5M steps, +126.94) but its Factory
aversion is baked in, and steps 2-4 change the reward landscape that produced it. Resuming would fight
the existing policy.

**6. Rotate the nation blocks to be egocentric — optional, and no longer the headline.** Still cheap and
still principled (players are already reordered this way; nations are not), but §2 showed it is not
causing the observed failure. Do it if you are changing the state layout anyway.

**7. GNN / attention encoder — last. Planned in detail: [`gnn_encoder_plan.md`](gnn_encoder_plan.md).**
The literature in §4 supports it and it subsumes step 6, but it is a substantial rewrite of
`GetStateVector` and the policy network. Do it after 1-5 so improvements are attributable — the plan
document opens by saying so, and should not be started until RL-4 has trained and been evaluated.

**8. Add an opponent curriculum.** `--opponents` is a fixed list today.

**Measurement:** `Tests/RLPerNationBehaviourTests.cs` now exists for exactly this — run it before and
after. The acceptance target is **≥1.5 factories built per nation-stint** (ceiling 2), matching the 1.54
the heuristic bots achieve, with
Investor back near 12-20%. Aggregate win rate would not have surfaced this: RL-3's overall averages look
unremarkable, and the defect is only visible when the slot distribution is broken out.

A caution the §2 reversal earns: **that test uses heuristic opponents.** The exported games included
other RL bots and a human. If RL-4 looks good here, confirm against the opponent mix it will actually
face before concluding anything.

---

## Sources

- [Dota 2 with Large Scale Deep Reinforcement Learning (OpenAI Five)](https://arxiv.org/pdf/1912.06680) — γ annealing 0.998 → 0.9997 for long-horizon credit assignment
- [Long-Term Planning and Situational Awareness in OpenAI Five](https://arxiv.org/pdf/1912.06721)
- [From Images to Connections: Can DQN with GNNs Learn the Strategic Game of Hex?](https://arxiv.org/pdf/2311.13414)
- [Graph Convolutional Networks for Turn-Based Strategy Games](https://www.researchgate.net/publication/358661169_Graph_Convolutional_Networks_for_Turn-Based_Strategy_Games)
- [Challenges and Opportunities in Deep RL with Graph Neural Networks: A Comprehensive Review](https://arxiv.org/pdf/2206.07922)
- [Using Graph Neural Networks in Reinforcement Learning: A Practical Guide (ICLR Blogposts 2026)](https://iclr-blogposts.github.io/2026/blog/2026/rl-with-gnns/)
- [A Survey on Transformers in Reinforcement Learning](https://arxiv.org/html/2301.03044v3) — Zambaldi relational attention → AlphaStar entity encoder
- [An Overdue Post on AlphaStar, Part 2](https://www.alexirpan.com/2019/02/22/alphastar-part2.html)
- [Entity-based Reinforcement Learning for Autonomous Cyber Defence](https://arxiv.org/html/2410.17647v3)
- [Discount Factor as a Regularizer in Reinforcement Learning](https://arxiv.org/pdf/2007.02040)
