export function formatMoney(cents: number, currency: string): string {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(cents / 100);
}

/** RFC 4180 CSV: every field quoted when it holds a comma, quote, or line break. */
export function toCsv(rows: (string | number | boolean | null | undefined)[][]): string {
  const field = (value: string | number | boolean | null | undefined) => {
    const text = value == null ? '' : String(value);
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  };
  return rows.map((row) => row.map(field).join(',')).join('\r\n') + '\r\n';
}

/** Starts a download of a text file in the browser. */
export function download(filename: string, text: string, type = 'text/csv'): void {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const link = Object.assign(document.createElement('a'), { href: url, download: filename });
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

export const ROLE_LABELS: Record<string, string> = { owner: 'Owner', admin: 'Admin', analyst: 'Analyst' };

/** What a role may do: admins and owners manage shows; owners manage members. */
export const can = {
  manage: (role: string | null | undefined) => role === 'admin' || role === 'owner',
  manageMembers: (role: string | null | undefined) => role === 'owner',
};
