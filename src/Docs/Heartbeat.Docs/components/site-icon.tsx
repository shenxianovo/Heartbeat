'use client';

import Image from 'next/image';
import { useSyncExternalStore } from 'react';

const subscribe = () => () => {};
const getServerIcon = () => 'windows';

function getBrowserIcon() {
  // iPad desktop mode also reports Macintosh, but supports multiple touch points.
  return /Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints <= 1
    ? 'macos'
    : 'windows';
}

export function SiteIcon() {
  const icon = useSyncExternalStore(subscribe, getBrowserIcon, getServerIcon);

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
