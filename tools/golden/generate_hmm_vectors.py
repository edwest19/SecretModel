# Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
#
# Generates tests/SecretModel.Tests/GoldenVectors/hmm.json: known answers for the HMM core
# (README SM-HMM-001, -003 and the Baum-Welch update). It does not run a forward-backward or Viterbi
# algorithm. It enumerates every possible hidden-state path and adds up exact probabilities with
# Python's fractions.Fraction, which is the "hand computation" the requirements ask for, done
# mechanically. The fixture's probabilities are dyadic (k / 2^n), so they are exact as doubles too.
#
# Requires only the Python standard library, plus numpy for one cross-check against the prototype.
# Run from the repository root:   python3 -I tools/golden/generate_hmm_vectors.py

import itertools
import json
import math
import os
import sys
from fractions import Fraction as F

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(REPO, "tests", "SecretModel.Tests", "GoldenVectors", "hmm.json")

# 3 states, 3 symbols. Every value is k / 2^n, so a double holds it exactly.
PI = [F(1, 2), F(1, 4), F(1, 4)]
A = [[F(1, 2), F(1, 4), F(1, 4)],
     [F(1, 8), F(5, 8), F(1, 4)],
     [F(3, 8), F(1, 8), F(1, 2)]]
B = [[F(1, 2), F(3, 8), F(1, 8)],
     [F(1, 8), F(1, 8), F(3, 4)],
     [F(1, 4), F(1, 2), F(1, 4)]]

O1 = [0, 2, 1, 1, 0, 2]   # 3^6 = 729 paths
O2 = [2, 2, 0, 1]         # 81 paths
O3 = [1]                  # a one-symbol sequence: it updates pi and b, but not a


def check_model(pi, a, b):
    assert sum(pi) == 1 and all(sum(r) == 1 for r in a) and all(sum(r) == 1 for r in b)


def joint(pi, a, b, path, obs):
    p = pi[path[0]] * b[path[0]][obs[0]]
    for t in range(1, len(obs)):
        p *= a[path[t - 1]][path[t]] * b[path[t]][obs[t]]
    return p


def paths(n, length):
    return itertools.product(range(n), repeat=length)


def probability(pi, a, b, obs):
    return sum(joint(pi, a, b, q, obs) for q in paths(len(pi), len(obs)))


def best_path(pi, a, b, obs):
    scored = sorted(((joint(pi, a, b, q, obs), q) for q in paths(len(pi), len(obs))), reverse=True)
    assert scored[0][0] > scored[1][0], "the best path must be unique"
    return list(scored[0][1]), scored[0][0]


def baum_welch_once(pi, a, b, sequences):
    # Expected counts by enumerating every path of every sequence, then the standard re-estimation:
    # pi from the first-step occupancy, a from transitions, b from emissions, each row normalized.
    n, m = len(pi), len(b[0])
    pi_num = [F(0)] * n
    a_num = [[F(0)] * n for _ in range(n)]
    b_num = [[F(0)] * m for _ in range(n)]
    for obs in sequences:
        total = probability(pi, a, b, obs)
        for q in paths(n, len(obs)):
            w = joint(pi, a, b, q, obs) / total
            pi_num[q[0]] += w
            for t in range(len(obs) - 1):
                a_num[q[t]][q[t + 1]] += w
            for t in range(len(obs)):
                b_num[q[t]][obs[t]] += w
    new_pi = [x / sum(pi_num) for x in pi_num]
    new_a = [[x / sum(row) for x in row] for row in a_num]
    new_b = [[x / sum(row) for x in row] for row in b_num]
    return new_pi, new_a, new_b


def floats(m):
    return [float(x) for x in m] if not isinstance(m[0], list) else [[float(x) for x in r] for r in m]


def main():
    check_model(PI, A, B)

    p1 = probability(PI, A, B, O1)
    ll1 = math.log(p1.numerator) - math.log(p1.denominator)   # exact fraction, no float underflow

    # Cross-check against the prototype's forward algorithm (reference/python), which should agree on
    # a sequence the model can produce.
    sys.path.insert(0, os.path.join(REPO, "reference", "python"))
    import numpy as np
    from app.hmm.model import SecretHMM
    proto = SecretHMM(n_states=3, n_symbols=3)
    proto.Pi, proto.A, proto.B = np.array(floats(PI)), np.array(floats(A)), np.array(floats(B))
    assert abs(proto.score_log_likelihood(O1) - ll1) < 1e-12, "prototype forward disagrees"

    vit_path, vit_p = best_path(PI, A, B, O1)

    new_pi, new_a, new_b = baum_welch_once(PI, A, B, [O1, O2, O3])
    total_before = sum(math.log(probability(PI, A, B, o)) for o in [O1, O2, O3])
    total_after = sum(math.log(probability(new_pi, new_a, new_b, o)) for o in [O1, O2, O3])
    assert total_after > total_before, "one EM step must not lower the likelihood"

    # Wikipedia, "Viterbi algorithm", worked example: states Healthy, Fever; symbols normal, cold, dizzy.
    w_pi = [F(6, 10), F(4, 10)]
    w_a = [[F(7, 10), F(3, 10)], [F(4, 10), F(6, 10)]]
    w_b = [[F(5, 10), F(4, 10), F(1, 10)], [F(1, 10), F(3, 10), F(6, 10)]]
    w_obs = [0, 1, 2]
    w_path, w_p = best_path(w_pi, w_a, w_b, w_obs)
    assert w_path == [0, 0, 1] and w_p == F(1512, 100000), "Wikipedia's published answer"

    doc = {
        "description": "HMM known answers (README SM-HMM-001, SM-HMM-003, Baum-Welch update) from "
                       "tools/golden/generate_hmm_vectors.py: exhaustive path enumeration in exact "
                       "fractions, not a forward-backward or Viterbi implementation.",
        "fixture": {"pi": floats(PI), "a": floats(A), "b": floats(B)},
        "forward": {
            "observations": O1,
            "probability": f"{p1.numerator}/{p1.denominator}",
            "log_likelihood": ll1,
        },
        "viterbi_fixture": {
            "observations": O1,
            "path": vit_path,
            "log_probability": math.log(vit_p.numerator) - math.log(vit_p.denominator),
        },
        "baum_welch_one_iteration": {
            "sequences": [O1, O2, O3],
            "log_likelihood_before": total_before,
            "log_likelihood_after": total_after,
            "pi": floats(new_pi), "a": floats(new_a), "b": floats(new_b),
        },
        "viterbi_wikipedia": {
            "source": "https://en.wikipedia.org/wiki/Viterbi_algorithm (worked example: Healthy/Fever; "
                      "normal, cold, dizzy). Published answer: path Healthy, Healthy, Fever with probability "
                      "0.01512, confirmed here by enumerating all 8 paths.",
            "pi": floats(w_pi), "a": floats(w_a), "b": floats(w_b),
            "observations": w_obs,
            "path": w_path,
            "probability": float(w_p),
        },
    }
    with open(OUT, "w", encoding="ascii", newline="\n") as f:
        f.write(json.dumps(doc, indent=1, sort_keys=True, ensure_ascii=True) + "\n")
    print(f"wrote {os.path.relpath(OUT, REPO)}")
    print(f"  P(O1) = {p1} ; log = {ll1!r}")
    print(f"  Viterbi(O1) = {vit_path} ; log p = {doc['viterbi_fixture']['log_probability']!r}")
    print(f"  Baum-Welch: total log-likelihood {total_before!r} -> {total_after!r}")
    print(f"  Wikipedia Viterbi: {w_path} p = {float(w_p)}")


if __name__ == "__main__":
    main()
