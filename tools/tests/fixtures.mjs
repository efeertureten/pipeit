// Shared fixtures (not a test file).
const P = (id, type, cells, dir, extra = {}) => Object.assign({ id, type, cells, dir }, extra);

/** GDD Level 8 reference, verbatim. */
export const LEVEL_8 = {
  level: 8, grid: [6, 6], tank: { x: 2, y: 2, w: 2, h: 2 }, leaks: 3,
  pipes: [
    P('p1', 'straight', [[0, 1], [1, 1]], 'left'),
    P('p2', 'straight', [[4, 4], [4, 5]], 'right'),
    P('p3', 'straight', [[5, 0], [5, 1]], 'up'),
    P('p4', 'elbow', [[4, 1], [4, 2], [5, 2]], 'right'),
    P('p5', 'straight', [[2, 5], [3, 5]], 'right'),
    P('p6', 'straight', [[2, 4], [3, 4]], 'down'),
  ],
};
