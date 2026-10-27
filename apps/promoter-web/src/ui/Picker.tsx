import { useEffect, useState } from 'react';

import { Field } from './components';

/**
 * A search box with results to pick from (artists, cities). `search` runs as the promoter types, after a short pause.
 * Once something is picked it shows with a "Change" button.
 */
export function Picker<T>({
  label, placeholder, search, describe, keyOf, value, onChange,
}: {
  label: string;
  placeholder?: string;
  search: (query: string) => Promise<T[]>;
  describe: (item: T) => string;
  keyOf: (item: T) => string;
  value: T | null;
  onChange: (item: T | null) => void;
}) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<T[]>([]);

  useEffect(() => {
    if (value || query.trim().length === 0) {
      setResults([]);
      return;
    }
    let cancelled = false;
    const timer = setTimeout(() => {
      void search(query.trim()).then((items) => {
        if (!cancelled) setResults(items);
      });
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [query, value, search]);

  if (value) {
    return (
      <div className="field">
        <span className="label-like"><strong>{label}:</strong> {describe(value)}</span>
        <button type="button" className="button secondary" onClick={() => { onChange(null); setQuery(''); }}>Change {label.toLowerCase()}</button>
      </div>
    );
  }
  return (
    <div className="field">
      <Field label={label} placeholder={placeholder} value={query} onChange={(e) => setQuery(e.target.value)} />
      {results.length > 0 && (
        <div className="picker" role="list" aria-label={`${label} results`}>
          {results.map((item) => (
            <button key={keyOf(item)} type="button" role="listitem" onClick={() => onChange(item)}>{describe(item)}</button>
          ))}
        </div>
      )}
    </div>
  );
}
