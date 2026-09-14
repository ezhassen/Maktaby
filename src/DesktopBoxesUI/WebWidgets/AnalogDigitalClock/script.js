let rafId = null;
let lastSecond = -1;
let lastMinute = -1;
function update() {
  if (window.__suspended || document.hidden) {
    setTimeout(() => { rafId = requestAnimationFrame(update); }, 1000);
    return;
  }
  const now = new Date();
  const s = now.getSeconds() + now.getMilliseconds() / 1000;
  const secInt = Math.floor(s);
  // Analog hands: update hour/minute only when minute changes, second every frame
  if (secInt !== lastSecond) {
    const h = now.getHours() % 12;
    const m = now.getMinutes();
    if (m !== lastMinute || secInt !== lastSecond) {
      const hourEl = document.getElementById('hour');
      const minuteEl = document.getElementById('minute');
      if (hourEl) hourEl.style.transform = `translateX(-50%) rotate(${h * 30 + m*0.5}deg)`;
      if (minuteEl) minuteEl.style.transform = `translateX(-50%) rotate(${m * 6 + s*0.1}deg)`;
      lastMinute = m;
    }
    lastSecond = secInt;
    // Digital time only once per second
    const timeEl = document.getElementById('time');
    const dateEl = document.getElementById('date');
    if (timeEl) timeEl.textContent = now.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', hour12: true });
    if (dateEl) dateEl.textContent = now.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' });
  }
  const secondEl = document.getElementById('second');
  if (secondEl) secondEl.style.transform = `translateX(-50%) rotate(${s*6}deg)`;
  rafId = requestAnimationFrame(update);
}
update();
window.addEventListener('suspend', () => { if (rafId) cancelAnimationFrame(rafId); rafId = null; });
window.addEventListener('resume', () => { lastSecond = -1; lastMinute = -1; if (!rafId) rafId = requestAnimationFrame(update); });
document.addEventListener('visibilitychange', () => { if (!document.hidden && !rafId) rafId = requestAnimationFrame(update); });
