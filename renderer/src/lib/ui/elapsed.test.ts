import { describe, expect, test } from "vitest";

import { formatElapsed } from "./elapsed";

describe("formatting how long a run has been going", () => {
  test("seconds are padded so the label does not jump width", () => {
    expect(formatElapsed(9_000)).toEqual({ minutes: "0", seconds: "09" });
  });

  test("minutes roll over at sixty seconds", () => {
    expect(formatElapsed(60_000)).toEqual({ minutes: "1", seconds: "00" });
    expect(formatElapsed(3_599_000)).toEqual({ minutes: "59", seconds: "59" });
  });

  test("minutes are not capped at an hour — a wedged run is the case this exists for", () => {
    expect(formatElapsed(3_600_000)).toEqual({ minutes: "60", seconds: "00" });
  });

  // Clocks move backwards (NTP, sleep/wake), and a negative duration must not render as "-1:-05".
  test("a clock that went backwards reads as zero", () => {
    expect(formatElapsed(-5_000)).toEqual({ minutes: "0", seconds: "00" });
  });

  test("a partial second is not counted", () => {
    expect(formatElapsed(999)).toEqual({ minutes: "0", seconds: "00" });
  });
});
