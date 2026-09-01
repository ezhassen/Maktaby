function update() {
  const now = new Date();
  const h = now.getHours() % 12;
  const m = now.getMinutes();
  const s = now.getSeconds() + now.getMilliseconds() / 1000;
  const hourEl = document.getElementById('hour');
  const minuteEl = document.getElementById('minute');
  const secondEl = document.getElementById('second');
  if (hourEl) hourEl.style.transform = `translateX(-50%) rotate(${h * 30 + m * 0.5}deg)`;
  if (minuteEl) minuteEl.style.transform = `translateX(-50%) rotate(${m * 6 + s * 0.1}deg)`;
  if (secondEl) secondEl.style.transform = `translateX(-50%) rotate(${s * 6}deg)`;
  const timeEl = document.getElementById('time');
  const dateEl = document.getElementById('date');
  if (timeEl) timeEl.textContent = now.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', hour12: true });
  if (dateEl) dateEl.textContent = now.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' });
  requestAnimationFrame(update);
}
update();
