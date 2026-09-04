let rafId = null;
let lastSecond = -1;
function update() {
  if (window.__suspended || document.hidden) {
    // Idle: throttle to 1fps, resume will kick rAF again
    setTimeout(() => { rafId = requestAnimationFrame(update); }, 1000);
    return;
  }
  const now = new Date();
  const s = now.getSeconds() + now.getMilliseconds()/1000;
  // Only touch DOM when second actually changed (saves layout thrash, ~60x less)
  if (Math.floor(s) !== lastSecond) {
    const h = now.getHours() % 12;
    const m = now.getMinutes();
    lastSecond = Math.floor(s);
    const hourEl = document.getElementById("hour");
    const minuteEl = document.getElementById("minute");
    if (hourEl) hourEl.style.transform = `translateX(-50%) rotate(${h*30 + m*0.5}deg)`;
    if (minuteEl) minuteEl.style.transform = `translateX(-50%) rotate(${m*6 + s*0.1}deg)`;
  }
  const secondEl = document.getElementById("second");
  if (secondEl) secondEl.style.transform = `translateX(-50%) rotate(${s*6}deg)`;
  rafId = requestAnimationFrame(update);
}
update();
window.addEventListener('suspend', () => { if (rafId) cancelAnimationFrame(rafId); rafId = null; });
window.addEventListener('resume', () => { lastSecond = -1; if (!rafId) rafId = requestAnimationFrame(update); });
document.addEventListener('visibilitychange', () => { if (!document.hidden && !rafId) rafId = requestAnimationFrame(update); });
