import { useId } from 'react';
import { localeNames, supportedLocales, t } from './i18n.ts';
import type { Locale } from './i18n.ts';
import { useLocale } from './i18nReact';
export default function LanguageSelector({ onChange }: { onChange: (locale: Locale) => void }) {
  const locale = useLocale();
  const id = useId();
  return (
    <label className="language-control" htmlFor={id}>
      <span>{t('Language')}</span>
      <select
        id={id}
        data-testid="language-selector"
        aria-label={t('Interface language')}
        value={locale}
        onChange={event => onChange(event.target.value as Locale)}
      >
        {supportedLocales.map(value => (
          <option key={value} value={value} lang={value}>
            {localeNames[value]}
          </option>
        ))}
      </select>
    </label>
  );
}
