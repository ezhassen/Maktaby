function update() {
  const now = new Date();
  const h = now.getHours() % 12;
  const m = now.getMinutes();
  const s = now.getSeconds() + now.getMilliseconds()/1000;
  document.getElementById("hour").style.transform = `translateX(-50%) rotate(${h*30 + m*0.5}deg)`;
  document.getElementById("minute").style.transform = `translateX(-50%) rotate(${m*6 + s*0.1}deg)`;
  document.getElementById("second").style.transform = `translateX(-50%) rotate(${s*6}deg)`;
  requestAnimationFrame(update);
}
update();
