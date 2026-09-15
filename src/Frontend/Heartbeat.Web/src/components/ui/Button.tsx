import type { ButtonHTMLAttributes } from "react";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "primary" | "outline" | "ghost" | "glass" | "glassPrimary";
  size?: "sm" | "default" | "icon";
}

export function Button({
  variant = "glass",
  size = "sm",
  className = "",
  type = "button",
  ...props
}: ButtonProps) {
  return (
    <button
      type={type}
      className={`ui-button ui-button-${variant} ui-button-${size} ${className}`}
      {...props}
    />
  );
}
