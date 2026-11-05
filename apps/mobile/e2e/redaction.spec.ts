import { expect, test } from '@playwright/test';
import { deflateSync } from 'node:zlib';

import { api, apiSession, artistWithAlbums, chooseHubCity, sql } from './fixtures';

/** Set to a directory to keep screenshots of the drawn box (for a report); the journey passes without it. */
const screenshots = process.env.E2E_SCREENSHOT_DIR;

test('staff draw a box over a quarantined photo with the mouse and archive it with the box blacked out', async ({ page, request }) => {
  // A fan's ticket photo, uploaded through the API the way the app does it. The redaction worker isn't running here,
  // so its verdict (submission verified, file sent to staff) is written to the database, as RedactionApiTests does.
  const fan = await apiSession(request);
  const { artist } = await artistWithAlbums(request, fan.token);
  const photo = ticketPng(800, 500);
  const intent = await api<{ upload_id: string; url: string; headers: Record<string, string> }>(
    request, fan.token, 'POST', '/uploads', { purpose: 'submission', mime_type: 'image/png', bytes: photo.length });
  const put = await request.fetch(intent.url, { method: 'PUT', headers: intent.headers, data: photo });
  expect(put.status(), await put.text()).toBe(204);
  const submission = await api<{ id: string }>(request, fan.token, 'POST', '/submissions', {
    artist_id: artist.id, kind: 'ticket', upload_ids: [intent.upload_id],
  });
  await sql(`UPDATE submissions SET status = 'verified' WHERE id = $1`, [submission.id]);
  const [file] = await sql<{ id: string }>(
    `UPDATE submission_files SET redaction_state = 'needs_review', redaction_note = $2 WHERE submission_id = $1 RETURNING id`,
    [submission.id, 'No barcode or QR code was found on this ticket. Check it by eye.'],
  );
  const fileId = file!.id;

  // Staff sign in through the app and open Photo redaction (Profile → Photo redaction, route /redactions).
  const staff = await apiSession(request, true);
  await page.goto('/sign-in');
  await page.getByLabel('Test account name').fill(staff.handle);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await chooseHubCity(page, 'Chicago'); // an account made through the API hasn't picked one yet
  await page.goto('/redactions');

  // Other waiting files (earlier runs on the same database) may be listed too: everything is scoped to this card.
  const card = page.getByTestId(`redaction-${fileId}`);
  await expect(card.getByText('No barcode or QR code was found on this ticket. Check it by eye.')).toBeVisible();
  const canvas = card.getByTestId(`photo-${fileId}`);
  await canvas.scrollIntoViewIfNeeded();
  // The canvas takes the photo's own aspect ratio (8:5 here, not the 4:3 it starts with) once the image is measured.
  await expect
    .poll(async () => {
      const b = await canvas.boundingBox();
      return b ? Math.round((b.width / b.height) * 100) / 100 : null;
    })
    .toBe(1.6);

  await card.getByRole('button', { name: 'Draw boxes' }).click();
  await expect(card.getByText('Drag over anything to black out. 0 boxes so far.')).toBeVisible();
  // Measured now: clicking the button may have scrolled the page, and the mouse works in viewport coordinates.
  const frame = (await canvas.boundingBox())!;

  // An order number in the top-right corner: drag from 60%,10% to 90%,30% of the photo.
  const drag = async () => {
    await page.mouse.move(frame.x + frame.width * 0.6, frame.y + frame.height * 0.1);
    await page.mouse.down();
    await page.mouse.move(frame.x + frame.width * 0.75, frame.y + frame.height * 0.2, { steps: 5 });
    await page.mouse.move(frame.x + frame.width * 0.9, frame.y + frame.height * 0.3, { steps: 5 });
    await page.mouse.up();
  };
  await drag();

  // The box overlay sits where the drag went (within a few pixels).
  const box = card.getByTestId(`box-${fileId}-0`);
  await expect(box).toBeVisible();
  const drawn = (await box.boundingBox())!;
  expect(drawn.x).toBeCloseTo(frame.x + frame.width * 0.6, -1);
  expect(drawn.y).toBeCloseTo(frame.y + frame.height * 0.1, -1);
  expect(drawn.width).toBeCloseTo(frame.width * 0.3, -1);
  expect(drawn.height).toBeCloseTo(frame.height * 0.2, -1);
  await expect(card.getByText('Drag over anything to black out. 1 box so far.')).toBeVisible();
  await expect(card.getByRole('button', { name: 'Archive with 1 box blacked out' })).toBeVisible();
  // The drag drew instead of scrolling the page.
  expect((await canvas.boundingBox())!.y).toBeCloseTo(frame.y, 0);

  await card.getByRole('button', { name: 'Remove last box' }).click();
  await expect(box).toHaveCount(0);
  await expect(card.getByRole('button', { name: 'Archive', exact: true })).toBeVisible();

  await drag();
  await expect(box).toBeVisible();
  await expect(card.getByRole('button', { name: 'Archive with 1 box blacked out' })).toBeVisible();
  if (screenshots) {
    await card.screenshot({ path: `${screenshots}/redaction-box-card.png` });
    await page.screenshot({ path: `${screenshots}/redaction-box-page.png` });
  }

  await card.getByRole('button', { name: 'Done drawing' }).click();
  // What the app sends: the body is read as the request is let through (the request seen after the fact may not
  // carry its body in Chrome).
  const decision = new Promise<{ action: string; boxes: { x: number; y: number; w: number; h: number }[] }>((resolve) =>
    page.route(`**/admin/redactions/${fileId}/decision`, async (route) => {
      resolve(route.request().postDataJSON());
      await route.continue();
    }),
  );
  await card.getByRole('button', { name: 'Archive with 1 box blacked out' }).click();
  const body = await decision;
  expect(body.action).toBe('archive');
  expect(body.boxes).toHaveLength(1);
  const sent = body.boxes[0]!;
  expect(sent.x).toBeGreaterThanOrEqual(0);
  expect(sent.y).toBeGreaterThanOrEqual(0);
  expect(sent.w).toBeGreaterThan(0);
  expect(sent.h).toBeGreaterThan(0);
  expect(sent.x + sent.w).toBeLessThanOrEqual(1);
  expect(sent.y + sent.h).toBeLessThanOrEqual(1);
  expect(sent.x).toBeCloseTo(0.6, 1);
  expect(sent.y).toBeCloseTo(0.1, 1);
  expect(sent.w).toBeCloseTo(0.3, 1);
  expect(sent.h).toBeCloseTo(0.2, 1);

  // The row leaves the list, and the API kept the box for the worker.
  await expect(card).toHaveCount(0);
  const [stored] = await sql<{ redaction_state: string; staff_boxes: unknown }>(
    'SELECT redaction_state, staff_boxes FROM submission_files WHERE id = $1', [fileId]);
  expect(stored!.redaction_state).toBe('approved');
  expect(stored!.staff_boxes).toEqual([sent]);
});

/** A PNG that looks like a ticket: a pale stub with a dark "order number" strip in its top-right corner. */
function ticketPng(width: number, height: number): Buffer {
  const rows: Buffer[] = [];
  for (let y = 0; y < height; y++) {
    const row = Buffer.alloc(1 + width * 3); // filter byte 0 (none), then RGB
    for (let x = 0; x < width; x++) {
      const strip = x > width * 0.62 && x < width * 0.88 && y > height * 0.12 && y < height * 0.28;
      const edge = x < 8 || y < 8 || x >= width - 8 || y >= height - 8;
      const [r, g, b] = strip ? [40, 40, 40] : edge ? [220, 120, 60] : [245, 236, 210];
      row.writeUInt8(r!, 1 + x * 3);
      row.writeUInt8(g!, 2 + x * 3);
      row.writeUInt8(b!, 3 + x * 3);
    }
    rows.push(row);
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8); // 8 bits per sample, RGB, no interlace
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    pngChunk('IHDR', header),
    pngChunk('IDAT', deflateSync(Buffer.concat(rows))),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

function pngChunk(type: string, data: Buffer): Buffer {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length, 0);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([length, body, crc]);
}

function crc32(data: Buffer): number {
  let crc = 0xffffffff;
  for (const byte of data) {
    crc ^= byte;
    for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}
