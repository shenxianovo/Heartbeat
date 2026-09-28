interface Circle<T> {
  item: T;
  x: number;
  y: number;
  radius: number;
}
export function pack<T extends { weight: number }>(
  items: T[],
  width: number,
): { circles: Circle<T>[]; height: number } {
  if (!items.length) return { circles: [], height: 300 };
  const largest = Math.max(1, Math.max(...items.map((item) => item.weight)));
  const circles: Circle<T>[] = [];
  for (const item of items) {
    const radius = Math.max(19, Math.sqrt(item.weight / largest) * 96);
    let candidate = { item, radius, x: 0, y: 0 };
    for (let attempt = 0; attempt < 12000; attempt++) {
      const angle = attempt * 2.39996;
      const distance = 6 * Math.sqrt(attempt);
      candidate = { item, radius, x: Math.cos(angle) * distance, y: Math.sin(angle) * distance };
      if (
        circles.every(
          (circle) =>
            Math.hypot(circle.x - candidate.x, circle.y - candidate.y) >=
            circle.radius + radius + 7,
        )
      )
        break;
    }
    circles.push(candidate);
  }
  const minX = Math.min(...circles.map((circle) => circle.x - circle.radius));
  const maxX = Math.max(...circles.map((circle) => circle.x + circle.radius));
  const minY = Math.min(...circles.map((circle) => circle.y - circle.radius));
  const maxY = Math.max(...circles.map((circle) => circle.y + circle.radius));
  const scale = Math.min((width - 16) / (maxX - minX), 500 / (maxY - minY));
  const offset = (width - (maxX - minX) * scale) / 2;
  return {
    height: (maxY - minY) * scale + 16,
    circles: circles.map((circle) => ({
      ...circle,
      x: (circle.x - minX) * scale + offset,
      y: (circle.y - minY) * scale + 8,
      radius: circle.radius * scale,
    })),
  };
}
