interface LoadingStateProps {
  label: string;
  fullPage?: boolean;
}

export function LoadingState({ label, fullPage = false }: LoadingStateProps) {
  return (
    <div className={fullPage ? "centered-page" : "status-state"} role="status">
      <span className="spinner" aria-hidden="true" />
      <span>{label}</span>
    </div>
  );
}
