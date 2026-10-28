'use client';

import Image from 'next/image';
import { useSyncExternalStore } from 'react';
import { getPlatformIcon } from '@/lib/platform-icon';

const subscribe = () => () => {};
const getServerIcon = () => 'windows';

function getBrowserIcon() {
  return getPlatformIcon(navigator);
}

function usePlatformIcon() {
  return useSyncExternalStore(subscribe, getBrowserIcon, getServerIcon);
}

export function SiteFavicon() {
  const icon = usePlatformIcon();
  // React hoists this link into <head>, with the same server fallback as the site icon.
  return <link rel="icon" href={`/favicon-${icon}.png`} type="image/png" sizes="48x48" />;
}

export function SiteIcon() {
  const icon = usePlatformIcon();
  return (
    <Image
      src={`/icon-${icon}.png`}
      alt=""
      width={32}
      height={32}
      sizes="32px"
      className="shrink-0"
    />
  );
}
