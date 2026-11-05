import { boxFromDrag, boxToFrame, containedFrame } from './boxes';

const view = { width: 400, height: 300 };

test('a contained image is centred in the view at its own aspect ratio', () => {
  // Same ratio: the frame is the view.
  expect(containedFrame(view, { width: 1400, height: 1050 })).toEqual({ left: 0, top: 0, width: 400, height: 300 });
  // Wider than the view: letterboxed top and bottom.
  expect(containedFrame(view, { width: 2000, height: 1000 })).toEqual({ left: 0, top: 50, width: 400, height: 200 });
  // Taller than the view: pillarboxed left and right.
  expect(containedFrame(view, { width: 600, height: 1200 })).toEqual({ left: 125, top: 0, width: 150, height: 300 });
  // Unknown size (or nothing laid out yet): the whole view.
  expect(containedFrame(view, null)).toEqual({ left: 0, top: 0, width: 400, height: 300 });
  expect(containedFrame({ width: 0, height: 0 }, { width: 10, height: 10 })).toEqual({ left: 0, top: 0, width: 0, height: 0 });
});

test('a drag becomes a box in fractions of the image frame, whichever way it was dragged', () => {
  const frame = containedFrame(view, { width: 1400, height: 1050 });
  expect(boxFromDrag({ x: 40, y: 30 }, { x: 160, y: 60 }, frame)).toEqual({ x: 0.1, y: 0.1, w: 0.3, h: 0.1 });
  expect(boxFromDrag({ x: 160, y: 60 }, { x: 40, y: 30 }, frame)).toEqual({ x: 0.1, y: 0.1, w: 0.3, h: 0.1 });
});

test('a drag is clamped to the image, not the view, and never passes the far edge', () => {
  const letterboxed = containedFrame(view, { width: 2000, height: 1000 }); // top 50, height 200
  // Starts above the image and ends past its right edge: clamped to the image.
  expect(boxFromDrag({ x: 300, y: 10 }, { x: 450, y: 150 }, letterboxed)).toEqual({ x: 0.75, y: 0, w: 0.25, h: 0.5 });
  // Rounding can't push x + w past 1.
  const box = boxFromDrag({ x: 1, y: 51 }, { x: 400, y: 250 }, letterboxed)!;
  expect(box.x + box.w).toBeLessThanOrEqual(1);
  expect(box.y + box.h).toBeLessThanOrEqual(1);
});

test('a tap or a sliver is not a box', () => {
  const frame = containedFrame(view, null);
  expect(boxFromDrag({ x: 40, y: 30 }, { x: 40, y: 30 }, frame)).toBeNull();
  expect(boxFromDrag({ x: 40, y: 30 }, { x: 200, y: 33 }, frame)).toBeNull();
  // Entirely outside the image (in the letterbox band).
  const letterboxed = containedFrame(view, { width: 2000, height: 1000 });
  expect(boxFromDrag({ x: 10, y: 5 }, { x: 100, y: 40 }, letterboxed)).toBeNull();
  expect(boxFromDrag({ x: 10, y: 5 }, { x: 100, y: 40 }, { left: 0, top: 0, width: 0, height: 0 })).toBeNull();
});

test('a box maps back onto the frame it was drawn on', () => {
  const frame = containedFrame(view, { width: 2000, height: 1000 });
  expect(boxToFrame({ x: 0.75, y: 0, w: 0.25, h: 0.5 }, frame)).toEqual({ left: 300, top: 50, width: 100, height: 100 });
});
