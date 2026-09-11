import unittest

from opponent_curriculum import OpponentCurriculum, previous_rl_opponents


class OpponentCurriculumTests(unittest.TestCase):
    def test_rl4_adds_only_earlier_rl_generations(self):
        curriculum = OpponentCurriculum("RL-4")

        self.assertEqual(("RL", "RL-2", "RL-3"), previous_rl_opponents("RL-4"))
        self.assertNotIn("RL-4", curriculum.opponents_at(1.0))
        self.assertEqual(
            ("Random", "Default", "Friendly", "Aggressive", "Greedy", "RL", "RL-2", "RL-3"),
            curriculum.opponents_at(1.0),
        )

    def test_heuristic_opponents_are_added_from_simple_to_advanced(self):
        curriculum = OpponentCurriculum("RL-4")

        self.assertEqual(("Random", "Default"), curriculum.opponents_at(0.0))
        self.assertEqual(("Random", "Default", "Friendly"), curriculum.opponents_at(0.20))
        self.assertEqual(
            ("Random", "Default", "Friendly", "Aggressive"),
            curriculum.opponents_at(0.40),
        )
        self.assertEqual(
            ("Random", "Default", "Friendly", "Aggressive", "Greedy"),
            curriculum.opponents_at(0.60),
        )

    def test_previous_rl_generations_are_introduced_one_at_a_time(self):
        curriculum = OpponentCurriculum("RL-4")

        self.assertEqual(
            ("Random", "Default", "Friendly", "Aggressive", "Greedy"),
            curriculum.opponents_at(0.74),
        )
        self.assertEqual("RL", curriculum.opponents_at(0.75)[-1])
        self.assertEqual("RL-2", curriculum.opponents_at(0.825)[-1])
        self.assertEqual("RL-3", curriculum.opponents_at(0.90)[-1])

    def test_each_target_excludes_itself_and_future_generations(self):
        self.assertEqual((), previous_rl_opponents("RL"))
        self.assertEqual(("RL",), previous_rl_opponents("RL-2"))
        self.assertEqual(("RL", "RL-2"), previous_rl_opponents("rl-3"))
        self.assertEqual((), previous_rl_opponents("CustomBot"))

    def test_progress_is_clamped(self):
        curriculum = OpponentCurriculum("RL-2")

        self.assertEqual(curriculum.opponents_at(0.0), curriculum.opponents_at(-1.0))
        self.assertEqual(curriculum.opponents_at(1.0), curriculum.opponents_at(2.0))


if __name__ == "__main__":
    unittest.main()
