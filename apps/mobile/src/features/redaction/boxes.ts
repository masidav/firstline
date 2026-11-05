import type { components } from '../../api/client';

/** A box as the API stores it: fractions of the image from its top-left corner (openapi.yaml RedactionBox). */
export type Box = components['schemas']['RedactionBox'];

export type Size = { width: number; height: number };
export type Point = { x: number; y: number };
export type Frame = { left: number; top: number; width: number; height: number };

/** The API refuses more (and so does the database): keep the screen honest about it. */
export const MAX_BOXES = 20;

/** A drag shorter than this (in view pixels) on either side is a tap, not a box. */
const MIN_DRAG_PX = 6;

/** Fractions are sent with this many decimals: fine enough for a 10 000 px image, and short on the wire. */
const DECIMALS = 4;

/**
 * Where an image drawn with `resizeMode="contain"` actually sits inside its view: the view, shrunk to the image's
 * aspect ratio and centred. Boxes are measured from this frame, not the view, so letterboxing doesn't shift them.
 * Without a known image size the frame is the whole view (the screen sets the view's aspect ratio to the image's
 * once it knows it, so the two then agree anyway).
 */
export function containedFrame(view: Size, image?: Size | null): Frame {
  if (!image || image.width <= 0 || image.height <= 0 || view.width <= 0 || view.height <= 0) {
    return { left: 0, top: 0, width: view.width, height: view.height };
  }
  const scale = Math.min(view.width / image.width, view.height / image.height);
  const width = image.width * scale;
  const height = image.height * scale;
  return { left: (view.width - width) / 2, top: (view.height - height) / 2, width, height };
}

/**
 * The box a drag from `start` to `end` (view coordinates) describes, as fractions of `frame`, clamped to it; null when
 * the drag is too short to be a box or lies entirely outside the frame.
 */
export function boxFromDrag(start: Point, end: Point, frame: Frame): Box | null {
  if (frame.width <= 0 || frame.height <= 0) return null;
  const clampX = (x: number) => Math.min(Math.max(x, frame.left), frame.left + frame.width);
  const clampY = (y: number) => Math.min(Math.max(y, frame.top), frame.top + frame.height);
  const left = clampX(Math.min(start.x, end.x));
  const right = clampX(Math.max(start.x, end.x));
  const top = clampY(Math.min(start.y, end.y));
  const bottom = clampY(Math.max(start.y, end.y));
  if (right - left < MIN_DRAG_PX || bottom - top < MIN_DRAG_PX) return null;

  const x = round((left - frame.left) / frame.width);
  const y = round((top - frame.top) / frame.height);
  // Width and height are rounded so that x + w and y + h never pass 1 (the API refuses a box past the edge).
  const w = Math.min(round((right - left) / frame.width), round(1 - x));
  const h = Math.min(round((bottom - top) / frame.height), round(1 - y));
  if (w <= 0 || h <= 0) return null;
  return { x, y, w, h };
}

/** Where to draw a box over the view: its frame coordinates in view pixels. */
export function boxToFrame(box: Box, frame: Frame): Frame {
  return {
    left: frame.left + box.x * frame.width,
    top: frame.top + box.y * frame.height,
    width: box.w * frame.width,
    height: box.h * frame.height,
  };
}

function round(n: number): number {
  return Math.round(n * 10 ** DECIMALS) / 10 ** DECIMALS;
}
