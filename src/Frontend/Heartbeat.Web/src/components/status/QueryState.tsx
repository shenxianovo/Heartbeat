import type { ReactNode } from "react";

interface QueryStateProps {
  eyebrow?: string;
  title: string;
  description: string;
  action?: ReactNode;
}

export function QueryState({ eyebrow, title, description, action }: QueryStateProps) {
  return (
    <section className="query-state">
      {eyebrow ? <span className="eyebrow">{eyebrow}</span> : null}
      <h2>{title}</h2>
      <p>{description}</p>
      {action ? <div className="query-state-action">{action}</div> : null}
    </section>
  );
}
